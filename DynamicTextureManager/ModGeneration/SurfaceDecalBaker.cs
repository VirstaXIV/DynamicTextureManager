using System;
using System.Numerics;
using DynamicTextureManager.DTextures.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Bakes a surface-projected decal into texture space: the decal is projected onto the
/// bind-pose mesh through a planar frame at its anchor point, then every mesh triangle is
/// rasterized in UV space with the projected decal sampled per texel. The result conforms
/// to the geometry, stretches with the actual UV density and continues across UV seams.
/// </summary>
public static class SurfaceDecalBaker
{
    /// <param name="previewHighlight">
    /// Draw id-remap texels as a bright highlight color instead of the raw row-pair value —
    /// the pair byte is almost invisible on an id map, so previews would look empty.
    /// </param>
    public static void Bake(Image<Rgba32> target, Image<Rgba32> decal, MaterialMesh mesh, DecalLayer layer, bool previewHighlight = false)
    {
        var anchor = new Vector3(layer.AnchorX, layer.AnchorY, layer.AnchorZ);
        var normal = new Vector3(layer.NormalX, layer.NormalY, layer.NormalZ);
        if (normal.LengthSquared() < 1e-6f || layer.WorldWidth <= 0f || layer.WorldHeight <= 0f)
            return;

        normal = Vector3.Normalize(normal);
        var (tangent, bitangent) = TangentFrame(normal, layer.RotationDeg);

        // Limit projection depth so the decal cannot wrap through the body onto the far side
        // or catch nearby lining/trim pieces as stray fragments.
        var maxDepth  = MathF.Max(layer.WorldWidth, layer.WorldHeight) * 0.4f;
        var threshold = (byte)Math.Clamp((int)Math.Round(layer.AlphaThreshold * 255f), 1, 255);
        var opacity   = Math.Clamp(layer.Opacity, 0f, 1f);

        if (layer.IdRemap && (layer.PaletteRows.Count == 0 || layer.PaletteRows.Count != layer.PaletteColors.Count))
        {
            DynamicTextureManager.Log.Warning("Surface colorset decal has no allocated rows, layer skipped.");
            return;
        }

        var decalPixels = new Rgba32[decal.Width * decal.Height];
        decal.CopyPixelDataTo(decalPixels);

        // Rasterize the same morphed surface that was clicked when placing.
        var indices = mesh.IndicesWithShapes(layer.SurfaceShapes);

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            // Stay on the stamped mesh part and off geometry that was hidden at stamp time —
            // overlapping pieces (linings, variant panels) must not catch the projection.
            var triangle = i / 3;
            if (layer.SurfaceLimitToPart && layer.SurfacePart >= 0 && mesh.TriangleParts[triangle] != layer.SurfacePart)
                continue;
            if ((mesh.TriangleAttributeMasks[triangle] & ~layer.SurfaceAttributes) != 0)
                continue;

            var i0 = indices[i];
            var i1 = indices[i + 1];
            var i2 = indices[i + 2];

            var p0 = mesh.Positions[i0];
            var p1 = mesh.Positions[i1];
            var p2 = mesh.Positions[i2];

            // Only surfaces facing the projector receive the decal — this also stops the
            // projection from hitting the far side of the mesh within the depth window.
            var faceNormal = Vector3.Cross(p1 - p0, p2 - p0);
            if (faceNormal.LengthSquared() < 1e-12f || Vector3.Dot(Vector3.Normalize(faceNormal), normal) < 0.2f)
                continue;

            // Decal-plane coordinates (0..1 across the decal) and projection depth per vertex.
            Vector3 Local(Vector3 p)
            {
                var d = p - anchor;
                return new Vector3(
                    Vector3.Dot(d, tangent) / layer.WorldWidth + 0.5f,
                    Vector3.Dot(d, bitangent) / layer.WorldHeight + 0.5f,
                    Vector3.Dot(d, normal));
            }

            var d0 = Local(p0);
            var d1 = Local(p1);
            var d2 = Local(p2);

            if ((d0.X < 0f && d1.X < 0f && d2.X < 0f) || (d0.X > 1f && d1.X > 1f && d2.X > 1f)
             || (d0.Y < 0f && d1.Y < 0f && d2.Y < 0f) || (d0.Y > 1f && d1.Y > 1f && d2.Y > 1f)
             || (MathF.Abs(d0.Z) > maxDepth && MathF.Abs(d1.Z) > maxDepth && MathF.Abs(d2.Z) > maxDepth))
                continue;

            RasterizeTriangle(target, decalPixels, decal.Width, decal.Height,
                mesh.Uvs[i0], mesh.Uvs[i1], mesh.Uvs[i2], d0, d1, d2, maxDepth, threshold, opacity, layer,
                previewHighlight);
        }
    }

    /// <summary> Rasterize one triangle in texture space, sampling the decal through the interpolated projection coordinates. </summary>
    private static void RasterizeTriangle(Image<Rgba32> target, Rgba32[] decal, int decalWidth, int decalHeight,
        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector3 d0, Vector3 d1, Vector3 d2,
        float maxDepth, byte threshold, float opacity, DecalLayer layer, bool previewHighlight)
    {
        var a = new Vector2(uv0.X * target.Width, uv0.Y * target.Height);
        var b = new Vector2(uv1.X * target.Width, uv1.Y * target.Height);
        var c = new Vector2(uv2.X * target.Width, uv2.Y * target.Height);

        var area = Cross(b - a, c - a);
        if (MathF.Abs(area) < 1e-6f)
            return;

        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var maxX = Math.Min(target.Width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var maxY = Math.Min(target.Height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (minX > maxX || minY > maxY)
            return;

        for (var y = minY; y <= maxY; ++y)
        {
            for (var x = minX; x <= maxX; ++x)
            {
                var p  = new Vector2(x + 0.5f, y + 0.5f);
                var w0 = Cross(b - p, c - p) / area;
                var w1 = Cross(c - p, a - p) / area;
                var w2 = 1f - w0 - w1;
                if (w0 < 0f || w1 < 0f || w2 < 0f)
                    continue;

                var local = d0 * w0 + d1 * w1 + d2 * w2;
                if (local.X is < 0f or > 1f || local.Y is < 0f or > 1f || MathF.Abs(local.Z) > maxDepth)
                    continue;

                var sample = SampleBilinear(decal, decalWidth, decalHeight, local.X, local.Y);
                if (layer.IdRemap)
                {
                    if (sample.A < threshold)
                        continue;

                    if (previewHighlight)
                    {
                        target[x, y] = new Rgba32(255, 140, 0, 255);
                        continue;
                    }

                    // R selects the claimed row's pair, G the half (255 = A, 0 = B).
                    var row   = layer.PaletteRows[DecalQuantizer.NearestIndex(sample, layer.PaletteColors)];
                    var pixel = target[x, y];
                    pixel.R      = (byte)(row / 2 * 17);
                    pixel.G      = row % 2 == 0 ? (byte)255 : (byte)0;
                    target[x, y] = pixel;
                }
                else
                {
                    var alpha = sample.A / 255f * opacity;
                    if (alpha <= 0f)
                        continue;

                    var pixel = target[x, y];
                    pixel.R      = (byte)Math.Clamp((int)Math.Round(sample.R * alpha + pixel.R * (1f - alpha)), 0, 255);
                    pixel.G      = (byte)Math.Clamp((int)Math.Round(sample.G * alpha + pixel.G * (1f - alpha)), 0, 255);
                    pixel.B      = (byte)Math.Clamp((int)Math.Round(sample.B * alpha + pixel.B * (1f - alpha)), 0, 255);
                    target[x, y] = pixel;
                }
            }
        }
    }

    /// <summary>
    /// The decal's axes on the surface: at rotation 0 the decal is upright on the gear.
    /// The axis directions are calibrated empirically against in-game results (2026-07-21:
    /// the analytic "viewer frame" rendered upside down) — change only with a visual check.
    /// </summary>
    public static (Vector3 Tangent, Vector3 Bitangent) TangentFrame(Vector3 normal, float rotationDeg)
    {
        var reference = Math.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
        var tangent   = -Vector3.Normalize(Vector3.Cross(normal, reference));
        var bitangent = Vector3.Cross(normal, tangent);
        if (Math.Abs(rotationDeg) > 0.001f)
        {
            var rotation = Quaternion.CreateFromAxisAngle(normal, rotationDeg * MathF.PI / 180f);
            tangent   = Vector3.Transform(tangent, rotation);
            bitangent = Vector3.Transform(bitangent, rotation);
        }

        return (tangent, bitangent);
    }

    private static float Cross(Vector2 a, Vector2 b)
        => a.X * b.Y - a.Y * b.X;

    private static Rgba32 SampleBilinear(Rgba32[] pixels, int width, int height, float u, float v)
    {
        var fx = u * (width - 1);
        var fy = v * (height - 1);
        var x0 = Math.Clamp((int)fx, 0, width - 1);
        var y0 = Math.Clamp((int)fy, 0, height - 1);
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var tx = fx - x0;
        var ty = fy - y0;

        var c00 = pixels[y0 * width + x0];
        var c10 = pixels[y0 * width + x1];
        var c01 = pixels[y1 * width + x0];
        var c11 = pixels[y1 * width + x1];

        float Lerp2(float a, float b, float t)
            => a + (b - a) * t;

        byte Channel(Func<Rgba32, byte> select)
            => (byte)Math.Clamp((int)Math.Round(
                Lerp2(Lerp2(select(c00), select(c10), tx), Lerp2(select(c01), select(c11), tx), ty)), 0, 255);

        return new Rgba32(Channel(p => p.R), Channel(p => p.G), Channel(p => p.B), Channel(p => p.A));
    }
}
