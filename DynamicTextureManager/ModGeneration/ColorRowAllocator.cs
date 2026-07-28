using System;
using System.Collections.Generic;
using System.Linq;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Assigns free colorset slots to a multi-color decal. The ID map's G channel BLENDS a
/// pair's A row toward its B row and the game samples it with interpolation, so edge texels
/// always mix the two halves of a pair. Two UNRELATED hues can therefore never share a pair
/// (their mix is a color the art doesn't contain — the verified fringe failure), but two
/// GRADIENT-COMPATIBLE colors (shades of one hue, black/white line art, outline + fill) can:
/// the interpolated mix is exactly the anti-aliasing the decal image carries, with G stamped
/// per texel from where the pixel sits between them. Colors with no compatible partner claim
/// a whole pair alone — A carries the color, B a darkened shade, the benign blend target.
/// </summary>
public static class ColorRowAllocator
{
    public const int RowCount  = 32;
    public const int PairCount = 16;

    /// <summary> Channel spread at or below which a color counts as achromatic (gray-scale). </summary>
    private const int AchromaticSpread = 28;

    /// <summary> Maximum hue distance in degrees for two chromatic colors to share a pair. </summary>
    private const float CompatibleHueDegrees = 35f;

    public sealed record AllocationResult(List<int> Rows, string? Error)
    {
        public bool Success
            => Error == null;
    }

    /// <summary> A pair-group of palette indices: the brighter color renders on the A half, the darker on B (-1 = none, B carries a shade). </summary>
    public readonly record struct PairGroup(int Light, int Dark);

    /// <summary>
    /// Group palette colors into gradient pairs so the decal claims the minimum number of
    /// slots: greedily match the most compatible color pairs (largest luminance gap first —
    /// a wide gradient covers the most in-between detail), leaving incompatible colors solo.
    /// The palette arrives luminance-sorted from extraction; each group keeps the brighter
    /// color as the A half so G = 255 always leans toward the light end.
    /// </summary>
    public static List<PairGroup> GroupGradientPairs(IReadOnlyList<uint> palette)
    {
        var candidates = new List<(int I, int J, float Gap)>();
        for (var i = 0; i < palette.Count; ++i)
        for (var j = i + 1; j < palette.Count; ++j)
        {
            if (!BlendCompatible(new Rgba32(palette[i]), new Rgba32(palette[j])))
                continue;

            candidates.Add((i, j, MathF.Abs(Luminance(new Rgba32(palette[i])) - Luminance(new Rgba32(palette[j])))));
        }

        var used   = new bool[palette.Count];
        var groups = new List<PairGroup>();
        foreach (var (i, j, _) in candidates.OrderByDescending(c => c.Gap))
        {
            if (used[i] || used[j])
                continue;

            used[i] = used[j] = true;
            // Extraction sorts brightest-first, so the lower index is the lighter color.
            groups.Add(new PairGroup(Math.Min(i, j), Math.Max(i, j)));
        }

        for (var i = 0; i < palette.Count; ++i)
            if (!used[i])
                groups.Add(new PairGroup(i, -1));

        return groups;
    }

    /// <summary>
    /// Whether the in-game A/B interpolation between two colors stays inside the decal's own
    /// look: gray-scale colors blend with anything achromatic, an outline/highlight neutral
    /// blends with any fill color (their mix is that color darkened or lightened — exactly
    /// what anti-aliased boundary pixels contain), and two chromatic colors only blend when
    /// they are shades of the same hue.
    /// </summary>
    private static bool BlendCompatible(Rgba32 a, Rgba32 b)
    {
        var achromA = IsAchromatic(a);
        var achromB = IsAchromatic(b);
        if (achromA || achromB)
            return true;

        var diff = MathF.Abs(Hue(a) - Hue(b));
        if (diff > 180f)
            diff = 360f - diff;
        return diff <= CompatibleHueDegrees;
    }

    private static bool IsAchromatic(Rgba32 c)
        => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= AchromaticSpread;

    private static float Hue(Rgba32 c)
    {
        float r = c.R, g = c.G, b = c.B;
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var d   = max - min;
        if (d <= 0f)
            return 0f;

        float h;
        if (max == r)
            h = (g - b) / d % 6f;
        else if (max == g)
            h = (b - r) / d + 2f;
        else
            h = (r - g) / d + 4f;

        h *= 60f;
        return h < 0f ? h + 360f : h;
    }

    private static float Luminance(Rgba32 c)
        => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;

    /// <summary>
    /// Pick one whole free pair per group, returning the A rows. Pairs the gear renders are
    /// blocked entirely — their texels carry intermediate G blends between both halves, so
    /// claiming either half would recolor the garment. Pairs any half of which another decal
    /// claims are blocked too.
    /// </summary>
    public static AllocationResult Allocate(int groupCount, IReadOnlySet<int> gearUsedPairs, IReadOnlySet<int> claimedRows)
    {
        var freePairs = new List<int>();
        for (var pair = 1; pair <= PairCount; ++pair)
        {
            var rowA = (pair - 1) * 2;
            if (gearUsedPairs.Contains(pair) || claimedRows.Contains(rowA) || claimedRows.Contains(rowA + 1))
                continue;

            freePairs.Add(pair);
        }

        if (freePairs.Count < groupCount)
            return new AllocationResult([],
                $"Decal needs {groupCount} free colorset slot(s) but only {freePairs.Count} are fully free on this material — raise Color Merge or remove other decals.");

        return new AllocationResult(freePairs.Take(groupCount).Select(pair => (pair - 1) * 2).ToList(), null);
    }
}
