using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Per-texel GEOMETRY lookup for hair adjustments. Hair textures pack strands into many
/// irregular UV pieces — flipped, partial, arbitrarily placed — so no UV-space coordinate can
/// say "where along the strand is this texel". The mesh can: roots sit near the skull, tips
/// far from it. This map rasterizes the editable triangles' world positions into texture space
/// and stores per texel the radial distance from the mesh centroid, percentile-normalized to
/// 0 (roots) .. 1 (tips) — continuous across every UV piece, immune to flipped or split
/// layouts. (An angular strand-identity coordinate was tried and removed: spherical mapping
/// produces pole starbursts and blobby patterns; strand identity works better in texture
/// space, where strands are laid out in parallel within each piece.)
/// Deterministic (pure mesh geometry), cached per mesh instance; built at a capped resolution
/// and sampled nearest.
/// </summary>
public sealed class HairGeoMap
{
    private const int MaxResolution = 1024;

    private readonly float[]   _d;
    private readonly int[]     _island;
    private readonly Vector2[] _polarity;
    private readonly int       _width;
    private readonly int       _height;

    public int IslandCount
        => _polarity.Length;

    private HairGeoMap(int width, int height, float[] d, int[] island, Vector2[] polarity)
    {
        _width    = width;
        _height   = height;
        _d        = d;
        _island   = island;
        _polarity = polarity;
    }

    /// <summary>
    /// Sample a texel of a (possibly larger) texture: the along-strand distance (0 roots ..
    /// 1 tips) and the texture piece (UV island) the texel belongs to. False = no geometry
    /// covers the texel.
    /// </summary>
    public bool TryGet(int x, int y, int textureWidth, int textureHeight, out float d, out int island)
    {
        var mx    = Math.Clamp(x * _width / Math.Max(1, textureWidth), 0, _width - 1);
        var my    = Math.Clamp(y * _height / Math.Max(1, textureHeight), 0, _height - 1);
        var index = my * _width + mx;
        d      = _d[index];
        island = _island[index];
        return d >= 0f && island >= 0;
    }

    /// <summary>
    /// A piece's root→tip POLARITY hint: the averaged texture-space gradient of the distance
    /// field, normalized. Too coarse to be the strand direction itself (pieces wrapping the
    /// head at near-constant skull distance defeat it) — its job is picking which END of an
    /// art-derived direction is the root.
    /// </summary>
    public Vector2 Polarity(int island)
        => island >= 0 && island < _polarity.Length ? _polarity[island] : new Vector2(0f, 1f);

    private static readonly ConditionalWeakTable<MaterialMesh, HairGeoMap> Cache = new();

    public static HairGeoMap Get(MaterialMesh mesh)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(mesh, out var map))
                Cache.Add(mesh, map = Build(mesh));
            return map!;
        }
    }

    private static HairGeoMap Build(MaterialMesh mesh)
    {
        var width  = MaxResolution;
        var height = MaxResolution;

        // Centroid of the editable geometry — the stand-in for the skull the strands grow from.
        var centroid = Vector3.Zero;
        var count    = 0;
        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            if (!mesh.TriangleEditable[i / 3])
                continue;

            centroid += mesh.Positions[mesh.Indices[i]] + mesh.Positions[mesh.Indices[i + 1]] + mesh.Positions[mesh.Indices[i + 2]];
            count    += 3;
        }

        if (count == 0)
            return new HairGeoMap(1, 1, [-1f], [-1], []);

        centroid /= count;

        // UV-island labeling via union-find over vertex indices — game meshes duplicate
        // vertices at UV seams, so index connectivity approximates the texture pieces. Each
        // piece later gets ONE averaged strand-flow direction: per-texel gradient directions
        // are far too noisy to use as a coordinate frame (verified: kaleidoscope artifacts),
        // while a piece's average orientation is exactly the "which way does this piece's
        // hair run" answer the patterns need.
        var parent = new int[mesh.Uvs.Length];
        for (var i = 0; i < parent.Length; ++i)
            parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }

        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            if (!mesh.TriangleEditable[i / 3])
                continue;

            var ra = Find(mesh.Indices[i]);
            var rb = Find(mesh.Indices[i + 1]);
            var rc = Find(mesh.Indices[i + 2]);
            parent[ra] = rc;
            parent[rb] = rc;
        }

        var idByRoot    = new Dictionary<int, int>();
        var islandCount = 0;

        // Rasterize interpolated world positions and island ids into texel space.
        var positions = new Vector3[width * height];
        var island    = new int[width * height];
        var covered   = new bool[width * height];
        Array.Fill(island, -1);
        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            if (!mesh.TriangleEditable[i / 3])
                continue;

            var root = Find(mesh.Indices[i]);
            if (!idByRoot.TryGetValue(root, out var islandId))
                idByRoot[root] = islandId = islandCount++;

            var pa = mesh.Positions[mesh.Indices[i]];
            var pb = mesh.Positions[mesh.Indices[i + 1]];
            var pc = mesh.Positions[mesh.Indices[i + 2]];
            var a  = mesh.Uvs[mesh.Indices[i]] * new Vector2(width, height);
            var b  = mesh.Uvs[mesh.Indices[i + 1]] * new Vector2(width, height);
            var c  = mesh.Uvs[mesh.Indices[i + 2]] * new Vector2(width, height);

            var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            var maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            var maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (MathF.Abs(area) < 1e-6f)
                continue;

            for (var y = minY; y <= maxY; ++y)
            for (var x = minX; x <= maxX; ++x)
            {
                var p  = new Vector2(x + 0.5f, y + 0.5f);
                var w0 = ((b.X - p.X) * (c.Y - p.Y) - (b.Y - p.Y) * (c.X - p.X)) / area;
                var w1 = ((c.X - p.X) * (a.Y - p.Y) - (c.Y - p.Y) * (a.X - p.X)) / area;
                var w2 = 1f - w0 - w1;
                if (w0 < 0f || w1 < 0f || w2 < 0f)
                    continue;

                var index = y * width + x;
                positions[index] = pa * w0 + pb * w1 + pc * w2;
                island[index]    = islandId;
                covered[index]   = true;
            }
        }

        // Dilate a few texels so the authored padding around each piece (which mip sampling
        // bleeds into) inherits the nearest geometry instead of having none.
        for (var pass = 0; pass < 4; ++pass)
        {
            var nextCovered = (bool[])covered.Clone();
            for (var y = 0; y < height; ++y)
            for (var x = 0; x < width; ++x)
            {
                var index = y * width + x;
                if (covered[index])
                    continue;

                var neighbor = -1;
                if (x > 0 && covered[index - 1])
                    neighbor = index - 1;
                else if (x + 1 < width && covered[index + 1])
                    neighbor = index + 1;
                else if (y > 0 && covered[index - width])
                    neighbor = index - width;
                else if (y + 1 < height && covered[index + width])
                    neighbor = index + width;

                if (neighbor >= 0)
                {
                    positions[index]   = positions[neighbor];
                    island[index]      = island[neighbor];
                    nextCovered[index] = true;
                }
            }

            covered = nextCovered;
        }

        // Radial distances, percentile-normalized (P5..P95) so outliers don't compress the range.
        var distances = new float[width * height];
        var sorted    = new List<float>(width * height / 16);
        for (var i = 0; i < distances.Length; ++i)
        {
            if (!covered[i])
            {
                distances[i] = -1f;
                continue;
            }

            distances[i] = Vector3.Distance(positions[i], centroid);
            if (i % 16 == 0)
                sorted.Add(distances[i]);
        }

        float d5 = 0f, d95 = 1f;
        if (sorted.Count > 16)
        {
            sorted.Sort();
            d5  = sorted[sorted.Count * 5 / 100];
            d95 = sorted[sorted.Count * 95 / 100];
            if (d95 - d5 < 1e-5f)
                (d5, d95) = (0f, MathF.Max(d95, 1e-5f));
        }

        var d = new float[width * height];
        for (var i = 0; i < d.Length; ++i)
            d[i] = distances[i] < 0f ? -1f : Math.Clamp((distances[i] - d5) / (d95 - d5), 0f, 1f);

        var polarity = BuildPolarity(d, island, islandCount, width, height);

        DynamicTextureManager.Log.Debug(
            $"Hair geometry map for {mesh.GamePath}: {count / 3} triangles, {islandCount} pieces, radial range {d5:F3}..{d95:F3}.");
        return new HairGeoMap(width, height, d, island, polarity);
    }

    /// <summary> Per-piece averaged texture-space gradient of the distance field, normalized — the root→tip polarity hint. </summary>
    private static Vector2[] BuildPolarity(float[] d, int[] island, int islandCount, int width, int height)
    {
        var sums = new Vector2[Math.Max(1, islandCount)];
        for (var y = 0; y < height; ++y)
        for (var x = 0; x < width; ++x)
        {
            var index = y * width + x;
            if (d[index] < 0f || island[index] < 0)
                continue;

            var left  = x > 0 && d[index - 1] >= 0f ? d[index - 1] : d[index];
            var right = x + 1 < width && d[index + 1] >= 0f ? d[index + 1] : d[index];
            var up    = y > 0 && d[index - width] >= 0f ? d[index - width] : d[index];
            var down  = y + 1 < height && d[index + width] >= 0f ? d[index + width] : d[index];
            sums[island[index]] += new Vector2(right - left, down - up);
        }

        for (var i = 0; i < sums.Length; ++i)
        {
            var length = sums[i].Length();
            sums[i] = length < 1e-6f ? new Vector2(0f, 1f) : sums[i] / length;
        }

        return sums;
    }
}
