using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Lifts a decal already baked into an id map (e.g. by a source mod) out into a stamp image,
/// so it becomes a movable, recolorable decal layer instead of fixed texels. Selection is
/// per ROW (a pair's A and B halves separately, by G-dominance): a baked decal often shares
/// a pair with the garment — e.g. the decal on 3B while 3A colors the cloth — so the
/// extracted content is later restamped onto freshly claimed pairs of its own, carrying its
/// per-texel blend weight in the stamp's alpha. Row indices are 0-based half-rows (0-31).
/// </summary>
public static class ColorsetDecalExtractor
{
    /// <param name="Stamp"> The lifted footprint: row-coded colors, alpha = blend weight of the selected half (128-255). </param>
    /// <param name="X"> Footprint bounding box in id-map texels. </param>
    /// <param name="FillPair"> Dominant pair of the surrounding texels — what the erase fills R with. </param>
    /// <param name="FillBlend"> Median G of those surrounding texels — what the erase fills G with. </param>
    /// <param name="Rows"> The selected source rows actually present in the footprint, in stamp-palette order. </param>
    /// <param name="RowColors"> Packed stamp color per source row (unique by construction); index-matches <paramref name="Rows"/>. </param>
    public sealed record Extraction(Image<Rgba32> Stamp, int X, int Y, int W, int H, int MapWidth, int MapHeight,
        int FillPair, int FillBlend, List<int> Rows, List<uint> RowColors);

    /// <summary>
    /// Extract the footprint of the given rows from an id map. A texel belongs to the row its
    /// G channel dominantly blends toward (A at G≥128, else B); its weight — how much of that
    /// half it actually renders — becomes the stamp's alpha, so restamping onto a fresh
    /// pair's A row reproduces the look with edge blends tapering into the pair's shade half.
    /// </summary>
    public static Extraction? Extract(DecodedTexture idMap, IReadOnlyCollection<int> rows, Vector3[] rowDiffuse,
        bool largestComponentOnly)
    {
        if (rows.Count == 0)
            return null;

        var width    = idMap.Width;
        var height   = idMap.Height;
        var selected = rows.ToHashSet();
        var mask     = new bool[width * height];
        var texRows  = new byte[width * height];
        var weights  = new byte[width * height];
        var anySet   = false;
        for (var i = 0; i < width * height; ++i)
        {
            var g = idMap.Rgba[i * 4 + 1];
            texRows[i] = (byte)IdMapTexel.Row(idMap.Rgba[i * 4], g);
            weights[i] = g >= 128 ? g : (byte)(255 - g);
            if (!selected.Contains(texRows[i]))
                continue;

            mask[i] = true;
            anySet  = true;
        }

        if (!anySet)
            return null;

        if (largestComponentOnly)
            mask = LargestComponent(mask, width, height);

        // Bounding box of the footprint.
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (var y = 0; y < height; ++y)
            for (var x = 0; x < width; ++x)
                if (mask[y * width + x])
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }

        if (maxX < 0)
            return null;

        var w = maxX - minX + 1;
        var h = maxY - minY + 1;

        // One unique stamp color per source row actually present, seeded from the row's
        // diffuse so the stamp is recognizable. Uniqueness matters: rows with identical
        // authored colors would otherwise collapse into one under nearest-mapping.
        var presentRows = new List<int>();
        for (var y = minY; y <= maxY; ++y)
            for (var x = minX; x <= maxX; ++x)
            {
                var i = y * width + x;
                if (mask[i] && !presentRows.Contains(texRows[i]))
                    presentRows.Add(texRows[i]);
            }

        var usedColors = new HashSet<uint>();
        var rowColors  = new List<uint>();
        var colorOf    = new Dictionary<int, Rgba32>();
        foreach (var row in presentRows)
        {
            var diffuse = rowDiffuse[row];
            var color = new Rgba32(
                (byte)Math.Clamp((int)Math.Round(diffuse.X * 255f), 0, 255),
                (byte)Math.Clamp((int)Math.Round(diffuse.Y * 255f), 0, 255),
                (byte)Math.Clamp((int)Math.Round(diffuse.Z * 255f), 0, 255));
            while (!usedColors.Add(color.PackedValue))
                color = new Rgba32((byte)((color.R + 3) % 256), color.G, (byte)((color.B + 7) % 256));
            colorOf[row] = color;
            rowColors.Add(color.PackedValue);
        }

        var stamp = new Image<Rgba32>(w, h);
        stamp.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < h; ++y)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < w; ++x)
                {
                    var i = (minY + y) * width + minX + x;
                    if (mask[i])
                    {
                        var color = colorOf[texRows[i]];
                        row[x] = new Rgba32(color.R, color.G, color.B, weights[i]);
                    }
                    else
                    {
                        row[x] = new Rgba32(0, 0, 0, 0);
                    }
                }
            }
        });

        var (fillPair, fillBlend) = FillValues(idMap, mask, width, height);
        return new Extraction(stamp, minX, minY, w, h, width, height, fillPair, fillBlend, presentRows, rowColors);
    }

    /// <summary> Keep only the largest 4-connected component of the footprint. </summary>
    private static bool[] LargestComponent(bool[] mask, int width, int height)
    {
        var labels = new int[mask.Length];
        var sizes  = new List<int> { 0 }; // label 0 = background
        var queue  = new Queue<int>();
        for (var start = 0; start < mask.Length; ++start)
        {
            if (!mask[start] || labels[start] != 0)
                continue;

            var label = sizes.Count;
            sizes.Add(0);
            labels[start] = label;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                ++sizes[label];
                var x = i % width;
                var y = i / width;
                Span<int> neighbors = [x > 0 ? i - 1 : -1, x < width - 1 ? i + 1 : -1, y > 0 ? i - width : -1, y < height - 1 ? i + width : -1];
                foreach (var n in neighbors)
                    if (n >= 0 && mask[n] && labels[n] == 0)
                    {
                        labels[n] = label;
                        queue.Enqueue(n);
                    }
            }
        }

        var best = 1;
        for (var l = 2; l < sizes.Count; ++l)
            if (sizes[l] > sizes[best])
                best = l;

        var result = new bool[mask.Length];
        for (var i = 0; i < mask.Length; ++i)
            result[i] = labels[i] == best;
        return result;
    }

    /// <summary>
    /// What the erased footprint gets filled with: the dominant pair of a 2-texel ring around
    /// the footprint and the median G (A/B blend) of that pair's ring texels — the closest
    /// stand-in for what the garment would have rendered there without the decal. When the
    /// decal shares its pair with the garment, the ring is dominated by the garment's own
    /// texels of that pair, so the fill blends straight back into the surrounding cloth.
    /// </summary>
    private static (int FillPair, int FillBlend) FillValues(DecodedTexture idMap, bool[] mask, int width, int height)
    {
        var ring = new List<int>();
        for (var y = 0; y < height; ++y)
            for (var x = 0; x < width; ++x)
            {
                var i = y * width + x;
                if (mask[i])
                    continue;

                var near = false;
                for (var dy = -2; dy <= 2 && !near; ++dy)
                    for (var dx = -2; dx <= 2 && !near; ++dx)
                    {
                        var nx = x + dx;
                        var ny = y + dy;
                        if (nx >= 0 && nx < width && ny >= 0 && ny < height && mask[ny * width + nx])
                            near = true;
                    }

                if (near)
                    ring.Add(i);
            }

        if (ring.Count == 0)
        {
            // Footprint touches everything — fall back to the map's most common pair.
            var counts = new Dictionary<int, int>();
            for (var i = 0; i < width * height; ++i)
            {
                var pair = IdMapTexel.Pair(idMap.Rgba[i * 4]);
                counts[pair] = counts.GetValueOrDefault(pair) + 1;
            }

            return (counts.OrderByDescending(kvp => kvp.Value).First().Key, 255);
        }

        var pairCounts = new Dictionary<int, int>();
        foreach (var i in ring)
        {
            var pair = IdMapTexel.Pair(idMap.Rgba[i * 4]);
            pairCounts[pair] = pairCounts.GetValueOrDefault(pair) + 1;
        }

        var fillPair = pairCounts.OrderByDescending(kvp => kvp.Value).First().Key;

        var blends = ring
            .Where(i => IdMapTexel.Pair(idMap.Rgba[i * 4]) == fillPair)
            .Select(i => (int)idMap.Rgba[i * 4 + 1])
            .OrderBy(g => g)
            .ToList();
        var fillBlend = blends[blends.Count / 2];
        return (fillPair, fillBlend);
    }
}
