using System;
using System.Collections.Generic;
using System.Numerics;

namespace DynamicTextureManager.ModGeneration;

/// <summary> The UV layout of the meshes using one material: its island boundary (seam) edges. </summary>
public sealed class UvLayout
{
    public required IReadOnlyList<(Vector2 A, Vector2 B)> Seams { get; init; }
    public required int TriangleCount { get; init; }

    /// <summary> Island boundaries: UV-quantized edges used by exactly one triangle. Context triangles map into a different texture and are skipped. </summary>
    internal static UvLayout Build(MaterialMesh mesh)
    {
        var edges = new Dictionary<(long, long), (Vector2 A, Vector2 B, int Count)>();
        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            if (!mesh.TriangleEditable[i / 3])
                continue;

            AddEdge(edges, mesh.Uvs[mesh.Indices[i]], mesh.Uvs[mesh.Indices[i + 1]]);
            AddEdge(edges, mesh.Uvs[mesh.Indices[i + 1]], mesh.Uvs[mesh.Indices[i + 2]]);
            AddEdge(edges, mesh.Uvs[mesh.Indices[i + 2]], mesh.Uvs[mesh.Indices[i]]);
        }

        var seams = new List<(Vector2, Vector2)>();
        foreach (var edge in edges.Values)
            if (edge.Count == 1)
                seams.Add((edge.A, edge.B));

        return new UvLayout
        {
            Seams         = seams,
            TriangleCount = mesh.TriangleCount,
        };
    }

    private static void AddEdge(Dictionary<(long, long), (Vector2, Vector2, int)> edges, Vector2 a, Vector2 b)
    {
        var (keyA, keyB) = (Quantize(a), Quantize(b));
        var key = keyA < keyB ? (keyA, keyB) : (keyB, keyA);
        edges[key] = edges.TryGetValue(key, out var existing)
            ? (existing.Item1, existing.Item2, existing.Item3 + 1)
            : (a, b, 1);
    }

    private static long Quantize(Vector2 uv)
        => ((long)Math.Round(uv.X * 8192) << 20) | ((long)Math.Round(uv.Y * 8192) & 0xFFFFF);
}
