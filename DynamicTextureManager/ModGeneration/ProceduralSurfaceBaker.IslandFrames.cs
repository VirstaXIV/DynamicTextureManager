using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration.Shaders;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

// Per-UV-island rigid pattern frames for cellular patterns: islands welded over shared
// UV corners, each averaging its texel-space flow direction and texel density.
public static partial class ProceduralSurfaceBaker
{
    private readonly record struct IslandFrame(Vector2 Along, Vector2 Across, float MetersPerTexel, float Offset);

    /// <summary>
    /// One rigid pattern frame per UV island for cellular patterns: triangles weld into
    /// islands over shared UV corners, and each island averages its texel-space flow
    /// direction (world flow solved through the UV derivatives — mirrored islands flip
    /// with the parametrization) and texel density. Indexed per triangle; entries with
    /// zero MetersPerTexel are unusable (degenerate islands).
    /// </summary>
    private static IslandFrame[] ComputeIslandFrames(MaterialMesh mesh, SurfaceFlowField.NaturalFlow natural,
        int width, int height)
    {
        var indices = mesh.Indices;
        var frames  = new IslandFrame[mesh.TriangleCount];

        var parent  = new List<int>();
        var uvNodes = new Dictionary<(int U, int V), int>();
        var triNode = new int[mesh.TriangleCount];
        Array.Fill(triNode, -1);

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x         = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b)
                parent[Math.Max(a, b)] = Math.Min(a, b);
        }

        int UvNode(Vector2 uv)
        {
            var key = ((int)MathF.Round(uv.X * 16384f), (int)MathF.Round(uv.Y * 16384f));
            if (uvNodes.TryGetValue(key, out var node))
                return node;

            node = parent.Count;
            parent.Add(node);
            uvNodes[key] = node;
            return node;
        }

        // Pass 1: weld islands and accumulate each triangle's texel-space flow.
        var flowSum = new Dictionary<int, (Vector2 Dir, float Density, float Weight, int MinTriangle)>();
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var triangle = i / 3;
            if (!mesh.TriangleEditable[triangle])
                continue;

            var i0 = indices[i];
            var i1 = indices[i + 1];
            var i2 = indices[i + 2];

            var a = new Vector2(mesh.Uvs[i0].X * width, mesh.Uvs[i0].Y * height);
            var b = new Vector2(mesh.Uvs[i1].X * width, mesh.Uvs[i1].Y * height);
            var c = new Vector2(mesh.Uvs[i2].X * width, mesh.Uvs[i2].Y * height);

            var area = Cross(b - a, c - a);
            if (MathF.Abs(area) < 1e-6f)
                continue;

            var n0 = UvNode(mesh.Uvs[i0]);
            Union(n0, UvNode(mesh.Uvs[i1]));
            Union(n0, UvNode(mesh.Uvs[i2]));
            triNode[triangle] = n0;

            var worldArea = Vector3.Cross(mesh.Positions[i1] - mesh.Positions[i0],
                mesh.Positions[i2] - mesh.Positions[i0]).Length() * 0.5f;
            var texelsPerMeter = worldArea > 1e-12f ? MathF.Sqrt(MathF.Abs(area) * 0.5f / worldArea) : 0f;

            var f = natural.Direction[i0] + natural.Direction[i1] + natural.Direction[i2];
            if (f.LengthSquared() < 1e-8f)
                continue;

            f = Vector3.Normalize(f);

            var e1   = mesh.Positions[i1] - mesh.Positions[i0];
            var e2   = mesh.Positions[i2] - mesh.Positions[i0];
            var duv1 = b - a;
            var duv2 = c - a;
            var det  = Cross(duv1, duv2);
            if (MathF.Abs(det) < 1e-9f)
                continue;

            var dPdu = (e1 * duv2.Y - e2 * duv1.Y) / det;
            var dPdv = (e2 * duv1.X - e1 * duv2.X) / det;
            var g11  = Vector3.Dot(dPdu, dPdu);
            var g12  = Vector3.Dot(dPdu, dPdv);
            var g22  = Vector3.Dot(dPdv, dPdv);
            var detG = g11 * g22 - g12 * g12;
            if (MathF.Abs(detG) < 1e-18f)
                continue;

            var fu  = (Vector3.Dot(f, dPdu) * g22 - Vector3.Dot(f, dPdv) * g12) / detG;
            var fv  = (Vector3.Dot(f, dPdv) * g11 - Vector3.Dot(f, dPdu) * g12) / detG;
            var dir = new Vector2(fu, fv);
            if (dir.LengthSquared() < 1e-12f)
                continue;

            dir = Vector2.Normalize(dir);

            var root   = Find(n0);
            var weight = MathF.Abs(area);
            flowSum[root] = flowSum.TryGetValue(root, out var sum)
                ? (sum.Dir + dir * weight, sum.Density + texelsPerMeter * weight, sum.Weight + weight,
                    Math.Min(sum.MinTriangle, triangle))
                : (dir * weight, texelsPerMeter * weight, weight, triangle);
        }

        // Pass 2: one frame per island, written to each of its triangles.
        var islandFrames = new Dictionary<int, IslandFrame>();
        foreach (var (root, sum) in flowSum)
        {
            var along   = sum.Dir.LengthSquared() > 1e-8f ? Vector2.Normalize(sum.Dir) : new Vector2(0f, 1f);
            var density = sum.Weight > 0f ? sum.Density / sum.Weight : 0f;
            islandFrames[root] = new IslandFrame(along, new Vector2(-along.Y, along.X),
                density > 0f ? 1f / density : 0f,
                ProceduralFields.Hash01(7331, sum.MinTriangle, 0, 0) * 97f);
        }

        for (var t = 0; t < mesh.TriangleCount; ++t)
            if (triNode[t] >= 0 && islandFrames.TryGetValue(Find(triNode[t]), out var frame))
                frames[t] = frame;

        return frames;
    }
}
