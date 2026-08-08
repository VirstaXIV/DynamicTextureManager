using System;
using System.Collections.Generic;
using System.Numerics;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Bind-pose geometry of all meshes using one material, concatenated: positions and normals
/// in model space, UVs and a triangle index list — everything surface decals need to pick
/// against in the 3D viewport and to bake with.
/// </summary>
public sealed class MaterialMesh
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] Uvs { get; init; }
    public required int[] Indices { get; init; }

    /// <summary> Per-triangle submesh attribute mask — used to skip variant geometry the game currently hides. </summary>
    public required uint[] TriangleAttributeMasks { get; init; }

    /// <summary> Per-triangle connected-part id: separate mesh pieces (lining, straps, panels) get distinct ids. </summary>
    public required int[] TriangleParts { get; init; }

    /// <summary>
    /// Whether a triangle belongs to the source material (true) or is context geometry from
    /// another material of the same model set (false) — shown dimmed for orientation, but its
    /// UVs live in a different texture, so it is never picked, seeded or baked onto.
    /// </summary>
    public required bool[] TriangleEditable { get; init; }

    public required int PartCount { get; init; }

    /// <summary>
    /// Per-triangle source-model index for merged model sets — the body canvas keeps the
    /// SmallClothes order (0 top/chest, 1 legs, 2 hands, 3 feet) even when a model fails to
    /// load; single-model reads are all 0. Drives per-body-part weights in procedural bakes.
    /// </summary>
    public required byte[] TriangleUnit { get; init; }

    /// <summary>
    /// Shape keys in the model's own order (indices must line up with the game's enabled-
    /// shape mask): index-buffer swaps redirecting triangles to morphed alternate vertices.
    /// Applied at pick time so the ray hits the shaped surface the player actually sees.
    /// </summary>
    public required (string Name, (int IndexPosition, int NewVertex)[] Swaps)[] Shapes { get; init; }

    /// <summary> Game path of the model this mesh came from (drives the equipment-slot lookup for attribute masks). </summary>
    public required string GamePath { get; init; }

    private (int[] Canonical, List<int>[] Neighbors)? _adjacency;

    /// <summary>
    /// Vertex adjacency for surface-following decal projection (<see cref="SurfaceDecalBaker.
    /// ComputeSurfaceProjection"/>): vertices are welded by position (0.1mm, matching
    /// ComputeParts in <see cref="MdlMeshReader"/>) so a walk crosses UV-seam
    /// vertex duplicates as one continuous surface, then connected along every triangle edge —
    /// including non-editable context triangles, so the walk sees the true connectivity of the
    /// underlying body even though only editable triangles are ever painted. Built lazily once
    /// and cached; reused across every decal placed on this mesh.
    /// </summary>
    public (int[] Canonical, List<int>[] Neighbors) GetOrBuildAdjacency()
    {
        if (_adjacency is { } cached)
            return cached;

        var canonical  = new int[Positions.Length];
        var byPosition = new Dictionary<(long, long, long), int>();
        for (var v = 0; v < Positions.Length; ++v)
        {
            var p   = Positions[v];
            var key = ((long)Math.Round(p.X * 10000), (long)Math.Round(p.Y * 10000), (long)Math.Round(p.Z * 10000));
            if (byPosition.TryGetValue(key, out var first))
                canonical[v] = first;
            else
            {
                byPosition[key] = v;
                canonical[v]    = v;
            }
        }

        var neighborSets = new HashSet<int>?[Positions.Length];

        void Connect(int a, int b)
        {
            if (a == b)
                return;
            (neighborSets[a] ??= []).Add(b);
            (neighborSets[b] ??= []).Add(a);
        }

        for (var i = 0; i + 2 < Indices.Length; i += 3)
        {
            var c0 = canonical[Indices[i]];
            var c1 = canonical[Indices[i + 1]];
            var c2 = canonical[Indices[i + 2]];
            Connect(c0, c1);
            Connect(c1, c2);
            Connect(c2, c0);
        }

        // Sorted so visit order never depends on hash-set iteration — geodesic walks
        // tie-break on neighbor order, and builds must be byte-identical across runs.
        var neighbors = new List<int>[Positions.Length];
        for (var v = 0; v < Positions.Length; ++v)
        {
            neighbors[v] = neighborSets[v] is { } set ? [.. set] : [];
            neighbors[v].Sort();
        }

        _adjacency = (canonical, neighbors);
        return _adjacency.Value;
    }

    public int VertexCount
        => Positions.Length;

    public int TriangleCount
        => Indices.Length / 3;

    /// <summary> The index buffer with a set of enabled shape keys applied (the base buffer for mask 0). </summary>
    public int[] IndicesWithShapes(uint shapeMask)
    {
        if (shapeMask == 0 || Shapes.Length == 0)
            return Indices;

        var result = (int[])Indices.Clone();
        for (var s = 0; s < Shapes.Length && s < 32; ++s)
        {
            if ((shapeMask & (1u << s)) == 0)
                continue;

            foreach (var (position, newVertex) in Shapes[s].Swaps)
                if (position < result.Length && newVertex < VertexCount)
                    result[position] = newVertex;
        }

        return result;
    }
}
