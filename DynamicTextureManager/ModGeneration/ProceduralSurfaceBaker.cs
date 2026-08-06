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
        TextureSlot? effectSlot = null, CharacterColors characterColors = default)
    {
        if (layer.Opacity <= 0f)
            return;

        var generated = GetOrGenerate(mesh, layer, target.Width, target.Height, characterColors);
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
        int width, int height, CharacterColors characterColors)
    {
        static string Tone(Vector3? v)
            => v is { } t ? FormattableString.Invariant($"{t.X:F3},{t.Y:F3},{t.Z:F3}") : "-";

        var tones = layer.TintFromSkin || layer.UseCharacterColors
            ? $"{Tone(characterColors.Skin)}|{Tone(characterColors.HairMain)}|{Tone(characterColors.HairHighlight)}"
            : "none";
        var key   = $"{layer.ContentHash()}|{width}x{height}|{tones}";
        var table = Cache.GetOrCreateValue(mesh);

        lock (table)
        {
            if (table.TryGetValue(key, out var hit))
            {
                table[key] = (hit.Fields, ++_cacheSeq);
                return hit.Fields;
            }
        }

        var fields = Generate(mesh, layer, width, height, characterColors);
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
        public required float[]   FlowPotential;
        public required float[]   Weight;
        public required float[]   TexelsPerMeter;

        /// <summary>
        /// Flow-aligned flat coordinates in meters (X across the flow, Y along it) from the
        /// texel's two nearest surface charts — geodesic unfoldings computed on the welded
        /// mesh, so they run CONTINUOUSLY across UV seams. Directional patterns evaluate in
        /// both and cross-fade by <see cref="ChartBlend"/>, blurring chart boundaries
        /// instead of cutting.
        /// </summary>
        public required Vector2[] FlowCoordA;

        public required Vector2[] FlowCoordB;

        /// <summary> Second-chart share, 0 = chart A only. </summary>
        public required float[] ChartBlend;

        /// <summary> Per-texel pattern offsets of the two charts, decorrelating them. </summary>
        public required float[] OffsetA;

        public required float[] OffsetB;
    }

    /// <summary>
    /// Rasterize every accepted triangle in texture space, interpolating world position and
    /// normal per texel. Directional layers additionally sample the two nearest surface
    /// charts: per triangle the charts are ranked by summed vertex weight (1/d² to the chart
    /// seed), then each texel interpolates both charts' flat coordinates and its cross-fade.
    /// Where UV regions are shared by several triangles the sample with the larger weight
    /// wins, tie-broken by triangle order — deterministic by construction. Single-threaded
    /// on purpose: the overlap resolution depends on visit order.
    /// </summary>
    private static SurfaceFields? RasterizeFields(int width, int height, MaterialMesh mesh, ProceduralSurfaceLayer layer)
    {
        var texels  = width * height;
        var flow    = SurfaceFlowField.ComputeVertexFlow(mesh, layer.Anchors);
        var natural = SurfaceFlowField.BodyFlow(mesh);
        var region  = ComputeRegionWeights(mesh, layer);
        var charts  = layer.Kind is SurfaceGeneratorKind.Fur or SurfaceGeneratorKind.Scales
            ? SurfaceFlowField.ComputeCharts(mesh, flow, layer.Anchors)
            : null;
        var fields = new SurfaceFields
        {
            Covered        = new bool[texels],
            Position       = new Vector3[texels],
            Normal         = new Vector3[texels],
            FlowPotential  = new float[texels],
            Weight         = new float[texels],
            TexelsPerMeter = new float[texels],
            FlowCoordA     = new Vector2[texels],
            FlowCoordB     = new Vector2[texels],
            ChartBlend     = new float[texels],
            OffsetA        = new float[texels],
            OffsetB        = new float[texels],
        };

        var indices = mesh.Indices;
        var any     = false;

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var triangle = i / 3;
            // Every editable triangle bakes, hidden variants included — full-coverage
            // patterns must exist wherever the surface can appear, and an attribute mask
            // captured on ONE canvas (the body) means something entirely different on a
            // companion canvas (the face) — gating on it once wiped the face bake.
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

            // Texel density of this triangle: how many texels one meter of surface covers,
            // from the texel-area / world-area ratio.
            var worldArea = Vector3.Cross(mesh.Positions[i1] - mesh.Positions[i0],
                mesh.Positions[i2] - mesh.Positions[i0]).Length() * 0.5f;
            var texelsPerMeter = worldArea > 1e-12f
                ? MathF.Sqrt(MathF.Abs(area) * 0.5f / worldArea)
                : 0f;

            // The triangle's two dominant charts by summed inverse-square seed distance,
            // ties broken by chart index. Adjacent triangles picking a different pair only
            // matters where the dropped chart's weight was tiny.
            var chartA = 0;
            var chartB = 0;
            if (charts != null)
            {
                var bestA = -1f;
                var bestB = -1f;
                for (var chart = 0; chart < charts.Count; ++chart)
                {
                    var d0 = charts.Distance[chart][i0];
                    var d1 = charts.Distance[chart][i1];
                    var d2 = charts.Distance[chart][i2];
                    if (d0 >= float.MaxValue || d1 >= float.MaxValue || d2 >= float.MaxValue)
                        continue;

                    var w = 1f / (d0 * d0 + 1e-4f) + 1f / (d1 * d1 + 1e-4f) + 1f / (d2 * d2 + 1e-4f);
                    if (w > bestA)
                    {
                        bestB  = bestA;
                        chartB = chartA;
                        bestA  = w;
                        chartA = chart;
                    }
                    else if (w > bestB)
                    {
                        bestB  = w;
                        chartB = chart;
                    }
                }

                if (bestB < 0f)
                    chartB = chartA;
            }

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

                    if (charts != null)
                    {
                        var localA = charts.Local[chartA];
                        var localB = charts.Local[chartB];
                        fields.FlowCoordA[index] = localA[i0] * w0 + localA[i1] * w1 + localA[i2] * w2;
                        fields.FlowCoordB[index] = localB[i0] * w0 + localB[i1] * w1 + localB[i2] * w2;
                        fields.OffsetA[index]    = charts.Offset[chartA];
                        fields.OffsetB[index]    = charts.Offset[chartB];

                        if (chartA != chartB)
                        {
                            var da = charts.Distance[chartA][i0] * w0 + charts.Distance[chartA][i1] * w1
                              + charts.Distance[chartA][i2] * w2;
                            var db = charts.Distance[chartB][i0] * w0 + charts.Distance[chartB][i1] * w1
                              + charts.Distance[chartB][i2] * w2;
                            var wa = 1f / (da * da + 1e-4f);
                            var wb = 1f / (db * db + 1e-4f);
                            fields.ChartBlend[index] = wb / (wa + wb);
                        }
                    }

                    fields.FlowPotential[index] = flow != null && (flow.HasFlow[i0] || flow.HasFlow[i1] || flow.HasFlow[i2])
                        ? flow.Potential[i0] * w0 + flow.Potential[i1] * w1 + flow.Potential[i2] * w2
                        : natural.Potential[i0] * w0 + natural.Potential[i1] * w1 + natural.Potential[i2] * w2;

                    any = true;
                }
            }
        }

        return any ? fields : null;
    }

    /// <summary>
    /// Per-vertex coverage weights: the painted brush dabs, applied in stroke order — erase
    /// dabs take the max fade, restore dabs peel it back — each with a smooth falloff band
    /// around its radius so the thinning transition has room. The face companion canvas is
    /// its own mesh and takes the face slider uniformly instead (the brush paints the body
    /// viewport). Null when nothing reduces coverage — the common case pays nothing.
    /// </summary>
    private static float[]? ComputeRegionWeights(MaterialMesh mesh, ProceduralSurfaceLayer layer)
    {
        if (mesh.GamePath.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase))
        {
            if (layer.WeightFace >= 1f)
                return null;

            var uniform = new float[mesh.VertexCount];
            Array.Fill(uniform, Math.Clamp(layer.WeightFace, 0f, 1f));
            return uniform;
        }

        if (layer.MaskDabs.Count == 0)
            return null;

        var count = mesh.VertexCount;
        var erase = new float[count];
        foreach (var dab in layer.MaskDabs)
        {
            var center   = new Vector3(dab.X, dab.Y, dab.Z);
            var radius   = MathF.Max(0.005f, dab.Radius);
            var outer    = radius * 1.4f;
            var outer2   = outer * outer;
            var inner    = radius * 0.6f;
            var strength = Math.Clamp(dab.Strength, 0f, 1f);

            for (var v = 0; v < count; ++v)
            {
                var d2 = (mesh.Positions[v] - center).LengthSquared();
                if (d2 > outer2)
                    continue;

                var falloff = strength * (1f - ProceduralFields.Smooth(inner, outer, MathF.Sqrt(d2)));
                if (falloff <= 0f)
                    continue;

                erase[v] = dab.Restore ? erase[v] * (1f - falloff) : MathF.Max(erase[v], falloff);
            }
        }

        var any    = false;
        var result = new float[count];
        for (var v = 0; v < count; ++v)
        {
            result[v] = 1f - erase[v];
            any      |= erase[v] > 0f;
        }

        return any ? result : null;
    }

    // ------------------------------------------------------------------ stage B: generators

    private static GeneratedFields? Generate(MaterialMesh mesh, ProceduralSurfaceLayer layer,
        int width, int height, CharacterColors characterColors)
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
        if (layer.UseCharacterColors)
        {
            // Fur/patterns color like the character's hair: main color as the base, the
            // highlight color on the crests — the same pair the game shades hair with.
            if (characterColors.HairMain is { } main)
                colorA = main;
            if (characterColors.HairHighlight is { } highlight)
                colorB = highlight;
        }

        if (layer.TintFromSkin && characterColors.Skin is { } tone)
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

                // Directional generators run in the texel's two nearest surface charts and
                // cross-fade, so chart boundaries blur instead of showing a hard seam.
                (float, float, float) Directional(Vector2 coord, float offset)
                    => layer.Kind == SurfaceGeneratorKind.Scales
                        ? EvaluateScales(layer, coord, offset, k)
                        : EvaluateFur(layer, surface.Position[index], surface.FlowPotential[index], coord, offset, k);

                (float Height, float AlbedoT, float Coverage) sample;
                if (layer.Kind is SurfaceGeneratorKind.Fur or SurfaceGeneratorKind.Scales)
                {
                    sample = Directional(surface.FlowCoordA[index], surface.OffsetA[index]);
                    var blend = surface.ChartBlend[index];
                    if (blend > 0.004f)
                    {
                        var other = Directional(surface.FlowCoordB[index], surface.OffsetB[index]);
                        sample = (
                            sample.Item1 + (other.Item1 - sample.Item1) * blend,
                            sample.Item2 + (other.Item2 - sample.Item2) * blend,
                            sample.Item3 + (other.Item3 - sample.Item3) * blend);
                    }
                }
                else
                {
                    sample = EvaluatePattern(layer, surface, index, k);
                }

                var (heightV, albedoT, coverage) = sample;

                heightV = ApplyContrast(heightV, layer.Contrast);

                // Region weights and exclusion fades THIN the pattern instead of ghosting
                // it: the fading weight becomes a survival threshold against the pattern's
                // own height, so strands break into sparser, shorter tufts toward bare skin
                // — a transition zone, not a translucent overlay. The threshold outruns the
                // tallest crest quickly (nothing survives below half weight), so stray
                // patches never linger deep inside a cleared area.
                var w = surface.Weight[index];
                if (w <= 0.001f)
                {
                    coverage = 0f;
                }
                else if (w < 0.999f)
                {
                    var cut = (1f - w) * 2.2f;
                    coverage *= ProceduralFields.Smooth(cut, cut + 0.2f, heightV);
                    heightV  *= 0.5f + 0.5f * w;
                }

                result.Coverage[index] = (byte)Math.Clamp((int)MathF.Round(coverage * 255f), 0, 255);
                result.Height[index]   = (ushort)Math.Clamp((int)MathF.Round(heightV * 65535f), 0, 65535);
                var albedo = Vector3.Lerp(colorA, colorB, albedoT);
                // Fur runs a value ramp on top: darker roots rising past full color at the
                // crests, centered so the typical coat tone stays the base color.
                if (layer.Kind == SurfaceGeneratorKind.Fur)
                    albedo *= 0.55f + 0.65f * heightV;
                result.Albedo[index] = (uint)(ToByte(albedo.X) | (ToByte(albedo.Y) << 8) | (ToByte(albedo.Z) << 16));
            }
        });

        Dilate(result, surface.Covered, width, height);
        return result;
    }

    /// <summary>
    /// Pad the bake outward into the unbaked gutter between UV islands: texels no triangle
    /// covers copy their nearest baked neighbor for a few rings. Without this, bilinear and
    /// mip sampling at an island's edge mixes in raw gutter texels — a one-pixel line of
    /// bare skin along every UV seam. Gated on the rasterizer's own coverage so legitimate
    /// zero-coverage texels INSIDE the canvas (the skin between spots) are never inflated.
    /// </summary>
    private static void Dilate(GeneratedFields fields, bool[] covered, int width, int height)
    {
        const int rings = 4;

        covered = (bool[])covered.Clone();
        var added = new List<(int Index, int From)>();
        for (var ring = 0; ring < rings; ++ring)
        {
            added.Clear();
            for (var y = 0; y < height; ++y)
            {
                var row = y * width;
                for (var x = 0; x < width; ++x)
                {
                    var index = row + x;
                    if (covered[index])
                        continue;

                    var from = -1;
                    if (x > 0 && covered[index - 1])
                        from = index - 1;
                    else if (x + 1 < width && covered[index + 1])
                        from = index + 1;
                    else if (y > 0 && covered[index - width])
                        from = index - width;
                    else if (y + 1 < height && covered[index + width])
                        from = index + width;

                    if (from >= 0)
                        added.Add((index, from));
                }
            }

            if (added.Count == 0)
                break;

            foreach (var (index, from) in added)
            {
                covered[index]               = true;
                fields.Coverage[index]       = fields.Coverage[from];
                fields.Height[index]         = fields.Height[from];
                fields.Albedo[index]         = fields.Albedo[from];
                fields.TexelsPerMeter[index] = fields.TexelsPerMeter[from];
            }
        }
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
        ProceduralSurfaceLayer layer, Vector2 coord, float offset, float k)
    {
        var q = new Vector2(
            coord.X * k + offset,
            coord.Y * k / MathF.Max(0.25f, layer.ScaleElongation) + offset);

        var w      = ProceduralFields.Worley(layer.Seed, q);
        var bevel  = MathF.Max(0.02f, layer.BevelWidth);
        var height = ProceduralFields.Smooth(0f, bevel, w.EdgeDist);

        var cellT   = (w.CellHash & 0xFFFFFF) / 16777215f;
        var albedoT = Math.Clamp(0.5f + (cellT - 0.5f) * 2f * layer.ColorVariation, 0f, 1f);

        return (height, albedoT, 1f);
    }

    /// <summary>
    /// Fur, built the way painted animal fur reads: strands GROUP into clumps (elongated
    /// cellular cells along the flow) separated by dark creases, and each strand is a sharp
    /// ridged-noise line. The coat wears the MAIN color throughout (dark roots, full color
    /// at the crests); the highlight color enters only through the coat MARKINGS — tabby
    /// bands wrapping the body, spots, marbled swirls — evaluated in world space so they
    /// continue seamlessly over every chart and canvas. Flecks add sparse lighter tips.
    /// </summary>
    private static (float Height, float AlbedoT, float Coverage) EvaluateFur(
        ProceduralSurfaceLayer layer, Vector3 pos, float potential, Vector2 coord, float island, float k)
    {
        var across = coord.X * k;
        var along  = coord.Y * k;

        // Slow wave: clumps and strands swing together along their length.
        var wave = layer.Curl * 3f
          * (ProceduralFields.Fbm3(layer.Seed + 909, new Vector3(across * 0.35f, along * 0.12f, island), 2) - 0.5f);
        var a = across + wave;

        // Clump layer: cells stretched hard along the flow, their lattice broken up by an
        // independent low-frequency warp (unwarped cells read as a diamond grid); the border
        // distance carves the darker separation between neighboring clumps.
        var warpX = (ProceduralFields.Fbm3(layer.Seed + 71, new Vector3(across * 0.5f, along * 0.2f, island), 2) - 0.5f) * 1.2f;
        var warpY = (ProceduralFields.Fbm3(layer.Seed + 72, new Vector3(across * 0.5f, along * 0.2f, island), 2) - 0.5f) * 0.6f;
        var clump      = ProceduralFields.Worley(layer.Seed + 1717, new Vector2(a * 1.4f + warpX + island, along * 0.22f + warpY + island));
        var separation = ProceduralFields.Smooth(0f, 0.5f, clump.EdgeDist);
        var clumpTone  = (clump.CellHash & 0xFFFFFF) / 16777215f;

        // Strand layer: sharp ridged lines at strand-aspect frequency, very elongated, the
        // island offset decorrelating separate UV pieces.
        var aspect = MathF.Max(1f, layer.StrandAspect);
        var strand = ProceduralFields.Ridged3(layer.Seed,
            new Vector3(a * aspect, along * aspect * 0.06f, island), 2);
        var fine = ProceduralFields.Ridged3(layer.Seed + 31,
            new Vector3(a * aspect * 2.3f, along * aspect * 0.16f, island), 2);

        // Strands carry the height, clump separation recesses it — floored so creases dim
        // rather than cut black holes.
        var height = Math.Clamp((0.3f + 0.7f * separation) * (0.25f + 0.55f * strand + 0.2f * fine), 0f, 1f);

        // The coat wears the main (hair) color; markings paint the highlight color over it.
        // Per-clump tone jitter feeds the shared variation slider; strands modulate the
        // marking edge slightly so it grows out of the coat instead of sitting on top.
        var marking = EvaluateMarkings(layer, pos, potential);
        var albedoT = Math.Clamp(marking * (0.8f + 0.2f * strand)
          + (clumpTone - 0.5f) * 2f * layer.ColorVariation * 0.35f, 0f, 1f);

        // Sparse brighter flecks, elongated along the flow — stray hairs catching the light.
        if (layer.SpeckDensity > 0f)
        {
            var speck = ProceduralFields.Worley(layer.Seed + 4242, new Vector2(a * 2.2f, along * 0.5f));
            var fleck = 1f - ProceduralFields.Smooth(0.04f, 0.16f, speck.F1);
            var gate  = ProceduralFields.Hash01(layer.Seed + 555, (int)speck.CellHash, 0, 0) < layer.SpeckDensity ? 1f : 0f;
            albedoT = Math.Clamp(albedoT + fleck * gate * 0.5f, 0f, 1f);
            height  = Math.Clamp(height + fleck * gate * 0.15f, 0f, 1f);
        }

        // The skin stays visible in the deepest clump separations — fur grows FROM the
        // skin, it doesn't wallpaper over it. Cubed so only the crease floors open up.
        var gap      = 1f - separation;
        var coverage = 1f - gap * gap * gap * 0.5f;

        return (height, albedoT, coverage);
    }

    /// <summary>
    /// Coat markings, 0 = main coat, 1 = highlight color: world-space fields at their own
    /// scale, so they read as the animal's pattern over the strand texture. Stripes band
    /// the flow potential — tabby rings wrapping the limbs and body.
    /// </summary>
    private static float EvaluateMarkings(ProceduralSurfaceLayer layer, Vector3 pos, float potential)
    {
        if (layer.Markings == FurMarkingStyle.None || layer.MarkingAmount <= 0f)
            return 0f;

        var km  = 1f / Math.Max(0.005f, layer.MarkingScaleCm / 100f);
        var cut = 1f - Math.Clamp(layer.MarkingAmount, 0f, 1f);
        switch (layer.Markings)
        {
            case FurMarkingStyle.Stripes:
            {
                var jitter = (ProceduralFields.Fbm3(layer.Seed + 811, pos * km, 3) - 0.5f) * 3f;
                var band   = (MathF.Sin(potential * km * MathF.PI + jitter) + 1f) * 0.5f;
                return ProceduralFields.Smooth(cut - 0.15f, cut + 0.15f, band);
            }
            case FurMarkingStyle.Spots:
            {
                var q = ProceduralFields.DomainWarp3(layer.Seed + 821, pos * (km * 0.5f), 0.35f);
                var v = ProceduralFields.Fbm3(layer.Seed + 822, q * 2f, 4);
                return ProceduralFields.Smooth(cut - 0.08f, cut + 0.08f, v);
            }
            default: // Marbling
            {
                var q     = ProceduralFields.DomainWarp3(layer.Seed + 831, pos * km, 0.6f);
                var v     = ProceduralFields.Fbm3(layer.Seed + 832, q, 5);
                var veinW = 0.03f + Math.Clamp(layer.MarkingAmount, 0f, 1f) * 0.2f;
                return 1f - ProceduralFields.Smooth(veinW * 0.3f, veinW, MathF.Abs(v - 0.5f));
            }
        }
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
                    // The crevice shade darkens AFTER the blend so skin peeking through the
                    // pattern's gaps sits in the pattern's shadow instead of glowing through.
                    var shade = 1f - cavity * (1f - generated.Height[index] / 65535f) * alpha;

                    ref var pixel = ref row[x];
                    pixel.R = ShadedBlend(pixel.R, packed & 0xFF, alpha, shade);
                    pixel.G = ShadedBlend(pixel.G, (packed >> 8) & 0xFF, alpha, shade);
                    pixel.B = ShadedBlend(pixel.B, (packed >> 16) & 0xFF, alpha, shade);
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
        // 2 cm scales read ~1 cm deep at maximum while fine fur stays subtler. BC7 and the
        // shader both soften the result — authored deliberately hot.
        var amplitude = Math.Clamp(layer.HeightStrength, 0f, 1f) * (layer.FeatureSizeCm / 100f) * 0.4f;
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

    private static byte ShadedBlend(byte baseValue, uint layerValue, float alpha, float shade)
        => (byte)Math.Clamp((int)MathF.Round((baseValue + ((float)layerValue - baseValue) * alpha) * shade), 0, 255);

    private static byte LerpByte(byte from, byte to, float t)
        => (byte)Math.Clamp((int)Math.Round(from + (to - from) * t), 0, 255);

    private static float Cross(Vector2 a, Vector2 b)
        => a.X * b.Y - a.Y * b.X;
}
