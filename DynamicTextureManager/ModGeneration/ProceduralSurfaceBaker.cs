using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration.Shaders;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Bakes a procedural surface layer (fur, scales, skin patterns) over the whole editable mesh
/// surface. The pattern is evaluated in world space at each texel's surface position, so it is
/// seamless across UV islands and material splits; a guide-anchor flow field orients it along
/// the body. Three stages: rasterize every accepted triangle into per-texel surface fields,
/// evaluate the generator into height/albedo/coverage planes, then compose the requested
/// output (diffuse colors, or relief/finish for sibling textures). The generated planes are a
/// pure function of (layer content, mesh, resolution) and are cached — the diffuse bake and
/// the sibling replays share one generation, and repeated composites are lookups.
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

        var generated = GetOrGenerate(mesh, layer, target.Width, target.Height, skinTone);
        if (generated == null)
            return;

        switch (effectSlot)
        {
            case null:
                ComposeDiffuse(target, generated, layer);
                break;
            default:
                // Relief (normal) and finish (mask) outputs arrive in a later stage.
                break;
        }
    }

    // ------------------------------------------------------------------ generation cache

    /// <summary> The generator's output planes at one resolution. Row-major, parallel arrays. </summary>
    private sealed class GeneratedFields
    {
        public required bool[]    Covered;
        public required float[]   Coverage;   // pattern presence 0..1 (already includes region weight)
        public required float[]   Height;     // relief field 0..1
        public required Vector3[] Albedo;     // linear-ish display RGB 0..1
        public required float[]   TexelsPerMeter;
    }

    // One generation is a pure function of (layer content, skin tone, mesh, W, H). Keyed per
    // mesh so entries die with the mesh; the small LRU absorbs diffuse + normal + mask
    // resolutions of the same material without thrashing during slider edits.
    private const int CachePerMesh = 4;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MaterialMesh,
        Dictionary<string, (GeneratedFields Fields, long Seq)>> Cache = new();

    private static long _cacheSeq;

    private static GeneratedFields? GetOrGenerate(MaterialMesh mesh, ProceduralSurfaceLayer layer,
        int width, int height, Vector3? skinTone)
    {
        var tone = layer.TintFromSkin && skinTone.HasValue
            ? $"{skinTone.Value.X:F3},{skinTone.Value.Y:F3},{skinTone.Value.Z:F3}"
            : "none";
        var key   = $"{layer.ContentHash()}|{width}x{height}|{tone}";
        var table = Cache.GetOrCreateValue(mesh);

        lock (table)
        {
            if (table.TryGetValue(key, out var hit))
            {
                table[key] = (hit.Fields, ++_cacheSeq);
                return hit.Fields;
            }
        }

        var fields = Generate(mesh, layer, width, height, skinTone);
        if (fields == null)
            return null;

        lock (table)
        {
            while (table.Count >= CachePerMesh)
            {
                string? oldest = null;
                var oldestSeq  = long.MaxValue;
                foreach (var (k, v) in table)
                    if (v.Seq < oldestSeq)
                    {
                        oldestSeq = v.Seq;
                        oldest    = k;
                    }

                if (oldest == null)
                    break;

                table.Remove(oldest);
            }

            table[key] = (fields, ++_cacheSeq);
        }

        return fields;
    }

    // ------------------------------------------------------------------ stage A: surface fields

    /// <summary> Per-texel surface samples the generators evaluate on. </summary>
    private sealed class SurfaceFields
    {
        public required bool[]    Covered;
        public required Vector3[] Position;
        public required Vector3[] Normal;
        public required Vector3[] Flow;
        public required float[]   Weight;
        public required float[]   TexelsPerMeter;
    }

    /// <summary>
    /// Rasterize every accepted triangle in texture space, interpolating world position,
    /// normal and flow per texel. Where UV regions are shared by several triangles the sample
    /// with the larger weight wins, tie-broken by triangle order — deterministic by
    /// construction. Single-threaded on purpose: the overlap resolution depends on visit order.
    /// </summary>
    private static SurfaceFields? RasterizeFields(int width, int height, MaterialMesh mesh, ProceduralSurfaceLayer layer)
    {
        var texels = width * height;
        var fields = new SurfaceFields
        {
            Covered        = new bool[texels],
            Position       = new Vector3[texels],
            Normal         = new Vector3[texels],
            Flow           = new Vector3[texels],
            Weight         = new float[texels],
            TexelsPerMeter = new float[texels],
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

            // Texel density of this triangle: how many texels one meter of surface covers,
            // from the texel-area / world-area ratio. Uniform per triangle is plenty — it
            // only scales relief gradients, not the pattern itself.
            var worldArea = Vector3.Cross(mesh.Positions[i1] - mesh.Positions[i0],
                mesh.Positions[i2] - mesh.Positions[i0]).Length() * 0.5f;
            var texelsPerMeter = worldArea > 1e-12f
                ? MathF.Sqrt(MathF.Abs(area) * 0.5f / worldArea)
                : 0f;

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
                    normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitY;
                    fields.Normal[index]         = normal;
                    fields.Flow[index]           = DefaultFlow(normal);
                    fields.TexelsPerMeter[index] = texelsPerMeter;
                    any = true;
                }
            }
        }

        return any ? fields : null;
    }

    /// <summary>
    /// Flow direction without guide anchors: straight down the body (gravity), projected onto
    /// the surface. Near-horizontal surfaces (shoulder tops) fall back to forward instead.
    /// </summary>
    private static Vector3 DefaultFlow(Vector3 normal)
    {
        var down = -Vector3.UnitY;
        var flow = down - normal * Vector3.Dot(down, normal);
        if (flow.LengthSquared() < 1e-4f)
        {
            var forward = Vector3.UnitZ;
            flow = forward - normal * Vector3.Dot(forward, normal);
            if (flow.LengthSquared() < 1e-8f)
                return Vector3.UnitZ;
        }

        return Vector3.Normalize(flow);
    }

    // ------------------------------------------------------------------ stage B: generators

    private static GeneratedFields? Generate(MaterialMesh mesh, ProceduralSurfaceLayer layer,
        int width, int height, Vector3? skinTone)
    {
        var surface = RasterizeFields(width, height, mesh, layer);
        if (surface == null)
            return null;

        var texels = width * height;
        var result = new GeneratedFields
        {
            Covered        = surface.Covered,
            Coverage       = new float[texels],
            Height         = new float[texels],
            Albedo         = new Vector3[texels],
            TexelsPerMeter = surface.TexelsPerMeter,
        };

        var colorA = Unpack(layer.ColorA);
        var colorB = Unpack(layer.ColorB);
        if (layer.TintFromSkin && skinTone is { } tone)
        {
            colorA = Vector3.Lerp(colorA, tone, 0.5f);
            colorB = Vector3.Lerp(colorB, tone, 0.5f);
        }

        // World scale: pattern frequency from the dominant feature size, resolution-free.
        var k = 1f / Math.Max(0.002f, layer.FeatureSizeCm / 100f);

        // Per-texel evaluation is a pure function of the sampled surface — deterministic
        // under any row scheduling.
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; ++x)
            {
                var index = y * width + x;
                if (!surface.Covered[index])
                    continue;

                var (heightV, albedoT, coverage) = layer.Kind switch
                {
                    SurfaceGeneratorKind.Scales => EvaluatePattern(layer, surface, index, k), // placeholder until the scales stage
                    SurfaceGeneratorKind.Fur    => EvaluatePattern(layer, surface, index, k), // placeholder until the fur stage
                    _                           => EvaluatePattern(layer, surface, index, k),
                };

                heightV = ApplyContrast(heightV, layer.Contrast);

                result.Coverage[index] = coverage * surface.Weight[index];
                result.Height[index]   = heightV;
                result.Albedo[index]   = Vector3.Lerp(colorA, colorB, albedoT);
            }
        });

        return result;
    }

    /// <summary>
    /// Skin patterns: world-space domain-warped fBm, thresholded into spots, flow-banded
    /// stripes or thin marbling veins. Color mixes between the two layer colors with a
    /// low-frequency variation field.
    /// </summary>
    private static (float Height, float AlbedoT, float Coverage) EvaluatePattern(
        ProceduralSurfaceLayer layer, SurfaceFields surface, int index, float k)
    {
        var pos = surface.Position[index];

        // Low-frequency color variation across the body, centered so 0 variation = pure A/B mix midpoint.
        var mix = ProceduralFields.Fbm3(layer.Seed + 7777, pos * (k * 0.15f), 2);
        var albedoT = Math.Clamp(0.5f + (mix - 0.5f) * 2f * layer.ColorVariation, 0f, 1f);

        // Threshold is exposed as "Amount" — more slider means more pattern, whatever the style.
        float coverage;
        switch (layer.PatternStyle)
        {
            case SurfacePatternStyle.Marbling:
            {
                // Thin veins: distance from the mid level-set of a strongly warped field;
                // the amount widens them.
                var q     = ProceduralFields.DomainWarp3(layer.Seed + 123, pos * k, layer.WarpStrength * 1.5f);
                var v     = ProceduralFields.Fbm3(layer.Seed, q, 5);
                var veinW = 0.02f + layer.Threshold * 0.18f;
                coverage = 1f - ProceduralFields.Smooth(veinW * 0.3f, veinW, MathF.Abs(v - 0.5f));
                break;
            }
            case SurfacePatternStyle.Stripes:
            {
                // Stripes need the geodesic flow potential — placed with guide anchors in the
                // flow stage. Until then: world-height bands jittered by noise, so the style
                // previews sensibly. The amount is the duty cycle.
                var jitter = (ProceduralFields.Fbm3(layer.Seed + 55, pos * k, 3) - 0.5f) * layer.WarpStrength * 4f;
                var s      = pos.Y * k * MathF.PI + jitter;
                var cut    = 1f - layer.Threshold;
                coverage = ProceduralFields.Smooth(cut - 0.15f, cut + 0.15f, (MathF.Sin(s) + 1f) * 0.5f);
                break;
            }
            default: // Spots
            {
                var q   = ProceduralFields.DomainWarp3(layer.Seed + 123, pos * (k * 0.5f), layer.WarpStrength);
                var v   = ProceduralFields.Fbm3(layer.Seed, q * 2f, 4);
                var cut = 1f - layer.Threshold;
                coverage = ProceduralFields.Smooth(cut - 0.08f, cut + 0.08f, v);
                break;
            }
        }

        return (coverage * 0.5f, albedoT, coverage);
    }

    private static float ApplyContrast(float v, float contrast)
        => Math.Clamp(0.5f + (v - 0.5f) * (contrast * 2f), 0f, 1f);

    // ------------------------------------------------------------------ stage C: composition

    /// <summary>
    /// Blend the generated colors into the target's RGB only — the target's alpha channel can
    /// carry material data (skin) and must survive the bake, same rule as color decals.
    /// Crevices darken by the cavity amount so the relief reads even before lighting.
    /// </summary>
    private static void ComposeDiffuse(Image<Rgba32> target, GeneratedFields generated, ProceduralSurfaceLayer layer)
    {
        var opacity = Math.Clamp(layer.Opacity, 0f, 1f);
        var cavity  = Math.Clamp(layer.CavityAmount, 0f, 1f);

        target.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; ++y)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; ++x)
                {
                    var index = y * accessor.Width + x;
                    if (!generated.Covered[index])
                        continue;

                    var alpha = generated.Coverage[index] * opacity;
                    if (alpha <= 0f)
                        continue;

                    var albedo = generated.Albedo[index] * (1f - cavity * (1f - generated.Height[index]));

                    ref var pixel = ref row[x];
                    pixel.R = LerpByte(pixel.R, ToByte(albedo.X), alpha);
                    pixel.G = LerpByte(pixel.G, ToByte(albedo.Y), alpha);
                    pixel.B = LerpByte(pixel.B, ToByte(albedo.Z), alpha);
                }
            }
        });
    }

    private static Vector3 Unpack(uint packed)
    {
        var c = new Rgba32(packed);
        return new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
    }

    private static byte ToByte(float v)
        => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    private static byte LerpByte(byte from, byte to, float t)
        => (byte)Math.Clamp((int)Math.Round(from + (to - from) * t), 0, 255);

    private static float Cross(Vector2 a, Vector2 b)
        => a.X * b.Y - a.Y * b.X;
}
