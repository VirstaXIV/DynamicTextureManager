using System;
using System.Numerics;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration.Shaders;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Bakes a procedural surface layer (fur, scales, skin patterns) over the whole editable mesh
/// surface. The pattern is evaluated in world space on the mesh, so it is seamless across UV
/// islands and material splits; a guide-anchor flow field orients it along the body.
/// Two passes: rasterize every accepted triangle into per-texel surface fields (position,
/// normal, coverage), then evaluate the generator and blend each covered texel exactly once —
/// triangles overlapping in UV space (shared islands, shared edges) never double-blend.
/// </summary>
public static class ProceduralSurfaceBaker
{
    /// <param name="effectSlot">
    /// When set, the bake targets a sibling texture of the same material (normal/mask):
    /// the footprint is identical, but each texel receives the layer's relief or finish
    /// instead of its colors.
    /// </param>
    public static void Bake(Image<Rgba32> target, MaterialMesh mesh, ProceduralSurfaceLayer layer,
        TextureSlot? effectSlot = null, Vector3? skinTone = null)
    {
        if (layer.Opacity <= 0f)
            return;

        // Sibling relief/finish outputs arrive in a later stage.
        if (effectSlot != null)
            return;

        var fields = RasterizeFields(target.Width, target.Height, mesh, layer);
        if (fields == null)
            return;

        ComposeDiffuse(target, fields, layer);
    }

    /// <summary> Per-texel surface fields the generators evaluate on. Parallel planes, row-major. </summary>
    private sealed class SurfaceFields
    {
        public required bool[]    Covered;
        public required Vector3[] Position;
        public required Vector3[] Normal;
        public required float[]   Weight;
    }

    /// <summary>
    /// Rasterize every accepted triangle in texture space, interpolating world position and
    /// normal per texel. Where UV regions are shared by several triangles the sample with the
    /// larger weight wins, tie-broken by triangle order — deterministic by construction.
    /// </summary>
    private static SurfaceFields? RasterizeFields(int width, int height, MaterialMesh mesh, ProceduralSurfaceLayer layer)
    {
        var texels = width * height;
        var fields = new SurfaceFields
        {
            Covered  = new bool[texels],
            Position = new Vector3[texels],
            Normal   = new Vector3[texels],
            Weight   = new float[texels],
        };

        var indices = mesh.Indices;
        var any     = false;

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var triangle = i / 3;
            if (!mesh.TriangleEditable[triangle])
                continue;
            if ((mesh.TriangleAttributeMasks[triangle] & ~layer.SurfaceAttributes) != 0)
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

            var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            var maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            var maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            if (minX > maxX || minY > maxY)
                continue;

            var invArea = 1f / area;
            for (var y = minY; y <= maxY; ++y)
            {
                for (var x = minX; x <= maxX; ++x)
                {
                    var p  = new Vector2(x + 0.5f, y + 0.5f);
                    var w0 = Cross(b - p, c - p) * invArea;
                    var w1 = Cross(c - p, a - p) * invArea;
                    var w2 = Cross(a - p, b - p) * invArea;
                    if (w0 < 0f || w1 < 0f || w2 < 0f)
                        continue;

                    var index  = y * width + x;
                    var weight = 1f;
                    if (fields.Covered[index] && fields.Weight[index] >= weight)
                        continue;

                    fields.Covered[index]  = true;
                    fields.Weight[index]   = weight;
                    fields.Position[index] = mesh.Positions[i0] * w0 + mesh.Positions[i1] * w1 + mesh.Positions[i2] * w2;
                    var normal = mesh.Normals[i0] * w0 + mesh.Normals[i1] * w1 + mesh.Normals[i2] * w2;
                    fields.Normal[index] = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitY;
                    any = true;
                }
            }
        }

        return any ? fields : null;
    }

    /// <summary>
    /// Blend the generated colors into the target's RGB only — the target's alpha channel can
    /// carry material data (skin) and must survive the bake, same rule as color decals.
    /// </summary>
    private static void ComposeDiffuse(Image<Rgba32> target, SurfaceFields fields, ProceduralSurfaceLayer layer)
    {
        var colorA  = new Rgba32(layer.ColorA);
        var opacity = Math.Clamp(layer.Opacity, 0f, 1f);

        target.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; ++y)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; ++x)
                {
                    var index = y * accessor.Width + x;
                    if (!fields.Covered[index])
                        continue;

                    var alpha = opacity * fields.Weight[index];
                    if (alpha <= 0f)
                        continue;

                    ref var pixel = ref row[x];
                    pixel.R = LerpByte(pixel.R, colorA.R, alpha);
                    pixel.G = LerpByte(pixel.G, colorA.G, alpha);
                    pixel.B = LerpByte(pixel.B, colorA.B, alpha);
                }
            }
        });
    }

    private static byte LerpByte(byte from, byte to, float t)
        => (byte)Math.Clamp((int)Math.Round(from + (to - from) * t), 0, 255);

    private static float Cross(Vector2 a, Vector2 b)
        => a.X * b.Y - a.Y * b.X;
}
