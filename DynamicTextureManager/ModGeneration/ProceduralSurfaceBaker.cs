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
            case TextureSlot.Normal when layer.WantsNormalEffect:
                ComposeNormal(target, generated, layer);
                break;
            case TextureSlot.Mask when layer.WantsMaskEffect || FinishMapping.ProceduralMaskWriteCavity:
                ComposeMask(target, generated, layer);
                break;
        }
    }

    // ------------------------------------------------------------------ generation cache

    /// <summary>
    /// The generator's output planes at one resolution. Row-major parallel arrays, kept
    /// compact on purpose — a 4K body texture is 16.7M texels and these live in the cache:
    /// coverage (pattern presence × exclusion weight) quantized to a byte, height to 16 bits,
    /// color packed. A zero coverage byte doubles as "texel not covered".
    /// </summary>
    private sealed class GeneratedFields
    {
        public required byte[]   Coverage;
        public required ushort[] Height;
        public required uint[]   Albedo;
        public required float[]  TexelsPerMeter;
    }

    // One generation is a pure function of (layer content, skin tone, mesh, W, H). Keyed per
    // mesh so entries die with the mesh; two entries absorb the common diffuse resolution
    // plus a differently-sized normal/mask sibling without thrashing during slider edits.
    private const int CachePerMesh = 2;

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
        public required float[]   FlowPotential;
        public required float[]   Weight;
        public required float[]   TexelsPerMeter;
        public required bool      HasAnchorFlow;
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
        var flow   = SurfaceFlowField.ComputeVertexFlow(mesh, layer.Anchors);
        var region = ComputeRegionWeights(mesh, layer);
        var fields = new SurfaceFields
        {
            Covered        = new bool[texels],
            Position       = new Vector3[texels],
            Normal         = new Vector3[texels],
            Flow           = new Vector3[texels],
            FlowPotential  = new float[texels],
            Weight         = new float[texels],
            TexelsPerMeter = new float[texels],
            HasAnchorFlow  = flow != null,
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

                    var index = y * width + x;

                    // The overlap tie-break compares final weights, so exclusion fades and
                    // region weights are part of the weight before the comparison.
                    var weight = flow != null
                        ? flow.Exclusion[i0] * w0 + flow.Exclusion[i1] * w1 + flow.Exclusion[i2] * w2
                        : 1f;
                    if (region != null)
                        weight *= region[i0] * w0 + region[i1] * w1 + region[i2] * w2;
                    if (fields.Covered[index] && fields.Weight[index] >= weight)
                        continue;

                    fields.Covered[index]  = true;
                    fields.Weight[index]   = weight;
                    fields.Position[index] = mesh.Positions[i0] * w0 + mesh.Positions[i1] * w1 + mesh.Positions[i2] * w2;
                    var normal = mesh.Normals[i0] * w0 + mesh.Normals[i1] * w1 + mesh.Normals[i2] * w2;
                    normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitY;
                    fields.Normal[index]         = normal;
                    fields.TexelsPerMeter[index] = texelsPerMeter;

                    // Anchor-driven flow interpolates across the triangle and re-projects onto
                    // the sampled normal; vertices no anchor reaches (and layers with no
                    // anchors) flow down the body by default.
                    if (flow != null && (flow.HasFlow[i0] || flow.HasFlow[i1] || flow.HasFlow[i2]))
                    {
                        var d = flow.Direction[i0] * w0 + flow.Direction[i1] * w1 + flow.Direction[i2] * w2;
                        d -= normal * Vector3.Dot(d, normal);
                        fields.Flow[index] = d.LengthSquared() > 1e-8f ? Vector3.Normalize(d) : DefaultFlow(normal);
                        fields.FlowPotential[index] = flow.Potential[i0] * w0 + flow.Potential[i1] * w1 + flow.Potential[i2] * w2;
                    }
                    else
                    {
                        fields.Flow[index]          = DefaultFlow(normal);
                        fields.FlowPotential[index] = fields.Position[index].Y;
                    }

                    any = true;
                }
            }
        }

        return any ? fields : null;
    }

    /// <summary>
    /// Per-vertex body-part weights from the merged canvas's model units (chest/legs/hands/
    /// feet sliders), averaged onto position-welded vertices and Jacobi-smoothed over the
    /// sorted adjacency so unit seams (wrists, waist, ankles) fade instead of cutting.
    /// Null when every weight is 1 — the common case pays nothing.
    /// </summary>
    private static float[]? ComputeRegionWeights(MaterialMesh mesh, ProceduralSurfaceLayer layer)
    {
        if (layer is { WeightChest: 1f, WeightLegs: 1f, WeightHands: 1f, WeightFeet: 1f })
            return null;

        var count = mesh.VertexCount;
        var (canonical, neighbors) = mesh.GetOrBuildAdjacency();

        var sum = new float[count];
        var hit = new int[count];
        for (var t = 0; t < mesh.TriangleCount; ++t)
        {
            var w = Math.Clamp(layer.UnitWeight(mesh.TriangleUnit[t]), 0f, 1f);
            for (var k = 0; k < 3; ++k)
            {
                var c = canonical[mesh.Indices[t * 3 + k]];
                sum[c] += w;
                hit[c] += 1;
            }
        }

        var weights = new float[count];
        for (var v = 0; v < count; ++v)
            weights[v] = hit[v] > 0 ? sum[v] / hit[v] : 1f;

        // Feather: plain Jacobi averaging over the welded graph — deterministic with the
        // sorted neighbor lists, and each iteration widens the transition by roughly one edge.
        var iterations = (int)MathF.Round(Math.Clamp(layer.RegionFeather, 0f, 1f) * 20f);
        for (var i = 0; i < iterations; ++i)
        {
            var next = new float[count];
            for (var v = 0; v < count; ++v)
            {
                if (canonical[v] != v)
                    continue;

                var total = weights[v];
                var n     = 1;
                foreach (var nb in neighbors[v])
                {
                    total += weights[nb];
                    ++n;
                }

                next[v] = total / n;
            }

            weights = next;
        }

        var result = new float[count];
        for (var v = 0; v < count; ++v)
            result[v] = weights[canonical[v]];

        return result;
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
            Coverage       = new byte[texels],
            Height         = new ushort[texels],
            Albedo         = new uint[texels],
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
                    SurfaceGeneratorKind.Scales => EvaluateScales(layer, surface, index, k),
                    SurfaceGeneratorKind.Fur    => EvaluateFur(layer, surface, index, k),
                    _                           => EvaluatePattern(layer, surface, index, k),
                };

                heightV = ApplyContrast(heightV, layer.Contrast);

                result.Coverage[index] = (byte)Math.Clamp((int)MathF.Round(coverage * surface.Weight[index] * 255f), 0, 255);
                result.Height[index]   = (ushort)Math.Clamp((int)MathF.Round(heightV * 65535f), 0, 65535);
                var albedo = Vector3.Lerp(colorA, colorB, albedoT);
                result.Albedo[index] = (uint)(ToByte(albedo.X) | (ToByte(albedo.Y) << 8) | (ToByte(albedo.Z) << 16));
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
                // Bands of the geodesic potential: with guide anchors the stripes wrap the
                // body perpendicular to the flow (tiger stripes); without anchors the
                // potential falls back to world height. The amount is the duty cycle.
                var jitter = (ProceduralFields.Fbm3(layer.Seed + 55, pos * k, 3) - 0.5f) * layer.WarpStrength * 4f;
                var s      = surface.FlowPotential[index] * k * MathF.PI + jitter;
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

    /// <summary>
    /// Scale plates: cellular noise in a flow-aligned anisotropic frame — cells stretch along
    /// the flow by the elongation factor, so plates lie like they grew with the body. Valid
    /// because plate size is far below the body's curvature radius; the projection's slow
    /// frame drift over large distances is invisible at centimeter features. Each plate is a
    /// beveled plateau (height from the distance to the cell border) with its own color.
    /// </summary>
    private static (float Height, float AlbedoT, float Coverage) EvaluateScales(
        ProceduralSurfaceLayer layer, SurfaceFields surface, int index, float k)
    {
        var pos = surface.Position[index];
        var f   = surface.Flow[index];
        var n   = surface.Normal[index];
        var c   = Vector3.Cross(n, f);

        var q = new Vector2(
            Vector3.Dot(pos, c) * k,
            Vector3.Dot(pos, f) * k / MathF.Max(0.25f, layer.ScaleElongation));

        var w      = ProceduralFields.Worley(layer.Seed, q);
        var bevel  = MathF.Max(0.02f, layer.BevelWidth);
        var height = ProceduralFields.Smooth(0f, bevel, w.EdgeDist);

        var cellT   = (w.CellHash & 0xFFFFFF) / 16777215f;
        var albedoT = Math.Clamp(0.5f + (cellT - 0.5f) * 2f * layer.ColorVariation, 0f, 1f);

        return (height, albedoT, 1f);
    }

    /// <summary>
    /// Fur: elongated noise ridges along the flow — the across-flow coordinate runs at
    /// strand-aspect frequency (curl-warped so strands wave instead of running straight),
    /// the along-flow coordinate stays low frequency, and the normal-offset axis decorrelates
    /// closely layered surfaces. Sparse cellular specks break the strands up into visible
    /// roots and tips. Color runs base-to-tip between the two layer colors.
    /// </summary>
    private static (float Height, float AlbedoT, float Coverage) EvaluateFur(
        ProceduralSurfaceLayer layer, SurfaceFields surface, int index, float k)
    {
        var pos = surface.Position[index];
        var f   = surface.Flow[index];
        var n   = surface.Normal[index];
        var c   = Vector3.Cross(n, f);

        var across = Vector3.Dot(pos, c) * k;
        var along  = Vector3.Dot(pos, f) * k;
        var depth  = Vector3.Dot(pos, n) * k;

        var curl = layer.Curl * 8f * (ProceduralFields.Fbm3(layer.Seed + 909, pos * k, 3) - 0.5f);
        var a    = across * MathF.Max(1f, layer.StrandAspect) + curl;

        var height = ProceduralFields.Fbm3(layer.Seed, new Vector3(a, along * 0.25f, depth), 4);

        // Sparse speck field: cell centers become dark roots/flecks, density-gated.
        if (layer.SpeckDensity > 0f)
        {
            var speck = ProceduralFields.Worley(layer.Seed + 4242, new Vector2(a * 0.5f, along * 2f));
            var spot  = 1f - ProceduralFields.Smooth(0.05f, 0.25f, speck.F1);
            height = Math.Clamp(height - spot * layer.SpeckDensity * 0.5f, 0f, 1f);
        }

        // Base-to-tip gradient plus the shared low-frequency variation.
        var mix     = ProceduralFields.Fbm3(layer.Seed + 7777, pos * (k * 0.15f), 2);
        var albedoT = Math.Clamp(height + (mix - 0.5f) * 2f * layer.ColorVariation * 0.5f, 0f, 1f);

        return (height, albedoT, 1f);
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
                    var alpha = generated.Coverage[index] / 255f * opacity;
                    if (alpha <= 0f)
                        continue;

                    var packed = generated.Albedo[index];
                    var shade  = 1f - cavity * (1f - generated.Height[index] / 65535f);

                    ref var pixel = ref row[x];
                    pixel.R = LerpByte(pixel.R, (byte)Math.Clamp((int)MathF.Round((packed & 0xFF) * shade), 0, 255), alpha);
                    pixel.G = LerpByte(pixel.G, (byte)Math.Clamp((int)MathF.Round(((packed >> 8) & 0xFF) * shade), 0, 255), alpha);
                    pixel.B = LerpByte(pixel.B, (byte)Math.Clamp((int)MathF.Round(((packed >> 16) & 0xFF) * shade), 0, 255), alpha);
                }
            }
        });
    }

    /// <summary>
    /// Bake the height field into the tangent-space normal map: central differences in texel
    /// space scaled to world units through the texel density, whiteout-blended over the
    /// existing normal detail (RG only, 128/128 neutral — B and A carry other channels in the
    /// character shader family and must survive). The relief amplitude scales with the feature
    /// size so bigger scales get proportionally deeper grooves. Green orientation is
    /// empirically unverified — <see cref="FinishMapping.ProceduralNormalFlipG"/> flips it
    /// without a rebuild of the plugin.
    /// </summary>
    private static void ComposeNormal(Image<Rgba32> target, GeneratedFields generated, ProceduralSurfaceLayer layer)
    {
        // Relief amplitude in meters at full strength: a fraction of the feature size, so
        // 2 cm scales read ~3 mm deep while fine fur stays subtle.
        var amplitude = Math.Clamp(layer.HeightStrength, 0f, 1f) * (layer.FeatureSizeCm / 100f) * 0.15f;
        if (amplitude <= 0f)
            return;

        var flipG = FinishMapping.ProceduralNormalFlipG ? -1f : 1f;

        target.ProcessPixelRows(accessor =>
        {
            var width  = accessor.Width;
            var height = accessor.Height;
            for (var y = 0; y < height; ++y)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; ++x)
                {
                    var index = y * width + x;
                    var cov   = generated.Coverage[index] / 255f;
                    if (cov <= 0f)
                        continue;

                    var texelsPerMeter = generated.TexelsPerMeter[index];
                    if (texelsPerMeter <= 0f)
                        continue;

                    var h = generated.Height[index] / 65535f;

                    // Neighbors from other UV islands would fake a cliff here; a covered
                    // neighbor with a wild height jump is treated as flat instead.
                    float Sample(int nx, int ny)
                    {
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                            return h;

                        var n = ny * width + nx;
                        if (generated.Coverage[n] == 0)
                            return h;

                        var hn = generated.Height[n] / 65535f;
                        return MathF.Abs(hn - h) > 0.5f ? h : hn;
                    }

                    var texelSize = 1f / texelsPerMeter;
                    var dx = (Sample(x + 1, y) - Sample(x - 1, y)) * amplitude / (2f * texelSize);
                    var dy = (Sample(x, y + 1) - Sample(x, y - 1)) * amplitude / (2f * texelSize);

                    var detail = Vector3.Normalize(new Vector3(-dx, -dy * flipG, 1f));

                    ref var pixel = ref row[x];
                    var bx = pixel.R / 255f * 2f - 1f;
                    var by = pixel.G / 255f * 2f - 1f;
                    var bz = MathF.Sqrt(MathF.Max(0f, 1f - bx * bx - by * by));

                    // Whiteout blend keeps both the base detail and the generated relief.
                    var combined = Vector3.Normalize(new Vector3(bx + detail.X, by + detail.Y, MathF.Max(1e-4f, bz * detail.Z)));

                    pixel.R = LerpByte(pixel.R, (byte)Math.Clamp((int)MathF.Round((combined.X * 0.5f + 0.5f) * 255f), 0, 255), cov);
                    pixel.G = LerpByte(pixel.G, (byte)Math.Clamp((int)MathF.Round((combined.Y * 0.5f + 0.5f) * 255f), 0, 255), cov);
                }
            }
        });
    }

    /// <summary>
    /// Push the layer's roughness shift into the mask map's roughness channel (semantics via
    /// <see cref="FinishMapping"/>), and optionally darken cavity/spec occlusion in crevices
    /// behind the runtime toggle — skin mask channels are empirical, one in-game session
    /// dials them in.
    /// </summary>
    private static void ComposeMask(Image<Rgba32> target, GeneratedFields generated, ProceduralSurfaceLayer layer)
    {
        var roughDelta = Math.Clamp(layer.RoughnessAmount, -1f, 1f) * (FinishMapping.MaskInvertRoughness ? -1f : 1f);
        var channel    = FinishMapping.MaskRoughnessChannel;
        var cavity     = FinishMapping.ProceduralMaskWriteCavity ? Math.Clamp(layer.CavityAmount, 0f, 1f) : 0f;

        target.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; ++y)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; ++x)
                {
                    var index = y * accessor.Width + x;
                    var cov   = generated.Coverage[index] / 255f;
                    if (cov <= 0f)
                        continue;

                    ref var pixel = ref row[x];
                    if (roughDelta != 0f)
                    {
                        var delta = (int)MathF.Round(roughDelta * 255f * cov);
                        switch (channel)
                        {
                            case 0:  pixel.R = (byte)Math.Clamp(pixel.R + delta, 0, 255); break;
                            case 2:  pixel.B = (byte)Math.Clamp(pixel.B + delta, 0, 255); break;
                            default: pixel.G = (byte)Math.Clamp(pixel.G + delta, 0, 255); break;
                        }
                    }

                    if (cavity > 0f)
                    {
                        var crevice = 1f - generated.Height[index] / 65535f;
                        pixel.R = (byte)Math.Clamp((int)MathF.Round(pixel.R * (1f - cavity * crevice * cov)), 0, 255);
                    }
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
