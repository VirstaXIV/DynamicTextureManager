using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration.Shaders;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

// Stage B of the procedural surface bake: evaluate the generator (fur, scales, skin
// patterns, markings) into height/albedo/coverage planes, then pad the UV gutters.
public static partial class ProceduralSurfaceBaker
{
    // ------------------------------------------------------------------ stage B: generators

    private static GeneratedFields? Generate(MaterialMesh mesh, ProceduralSurfaceLayer layer,
        int width, int height, CharacterColors characterColors, MarkingPatternImage? markingPattern)
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

        // World scale: pattern frequency from the ACTIVE kind's feature size, resolution-free.
        var k = 1f / Math.Max(0.001f, layer.ActiveSizeCm / 100f);

        // Per-texel evaluation is a pure function of the sampled surface — deterministic
        // under any row scheduling.
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; ++x)
            {
                var index = y * width + x;
                if (!surface.Covered[index])
                    continue;

                (float Height, float AlbedoT, float Coverage) sample;
                if (layer.Kind == SurfaceGeneratorKind.Scales)
                {
                    // Scales never cross-fade or switch frames (blending ghosts the plates,
                    // switching stripes them): each UV island carries ONE rigid frame, so
                    // cells stay uniform everywhere.
                    sample = EvaluateScales(layer, surface.FlowCoordA[index], surface.OffsetA[index], k);
                }
                else if (layer.Kind == SurfaceGeneratorKind.Fur)
                {
                    static (float, float, float) Mix((float, float, float) a, (float, float, float) b, float t)
                        => (a.Item1 + (b.Item1 - a.Item1) * t,
                            a.Item2 + (b.Item2 - a.Item2) * t,
                            a.Item3 + (b.Item3 - a.Item3) * t);

                    (float, float, float) Directional(Vector2 coord, float offset)
                        => EvaluateFur(layer, coord, offset, k);

                    // Near the mesh's open boundary (and on world-only canvases like the
                    // face) the pattern lives in a shared world frame — a cylinder around
                    // the body axis — so canvases meeting there arrive at the SAME pattern.
                    var seam = surface.SeamBlend[index];
                    if (seam <= 0.004f)
                    {
                        sample = Directional(WorldFrame(surface.Position[index]), 0f);
                    }
                    else
                    {
                        var blendB = surface.BlendB[index];
                        var blendC = surface.BlendC[index];
                        sample = Directional(surface.FlowCoordA[index], surface.OffsetA[index]);
                        if (blendB > 0.004f)
                            sample = Mix(sample, Directional(surface.FlowCoordB[index], surface.OffsetB[index]),
                                blendB / MathF.Max(1e-4f, 1f - blendC));
                        if (blendC > 0.004f)
                            sample = Mix(sample, Directional(surface.FlowCoordC[index], surface.OffsetC[index]), blendC);

                        if (seam < 0.996f)
                            sample = Mix(Directional(WorldFrame(surface.Position[index]), 0f), sample, seam);
                    }
                }
                else
                {
                    sample = EvaluatePattern(layer, surface, index, k);
                }

                var (heightV, jitter, coverage) = sample;

                heightV = ApplyContrast(heightV, layer.Contrast);

                // Markings place the highlight color over the base, on every kind: a
                // generated world-space field (tabby bands, spots, marbling) or the painted
                // mask. Generators contribute only their tonal jitter around the base.
                var marking = layer.Markings switch
                {
                    FurMarkingStyle.None    => 0f,
                    FurMarkingStyle.Painted => surface.MarkingPaint?[index] ?? 0f,
                    FurMarkingStyle.Custom  => EvaluateCustomMarkings(layer, markingPattern, surface.Position[index], surface.Normal[index]),
                    _                       => EvaluateMarkings(layer, surface.Position[index], surface.FlowPotential[index]),
                };

                // Marking edges mix WITH the coat: the pattern's own height field (strand
                // crests, plate tops) dithers the boundary, so the colors hand over strand
                // by strand along the flow instead of drawing a smooth line.
                if (marking is > 0.001f and < 0.999f)
                    marking = ProceduralFields.Smooth(0.35f, 0.65f, marking + (heightV - 0.5f) * 0.6f);

                var albedoT = Math.Clamp(marking + jitter, 0f, 1f);

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
                // crests, biased bright so a white coat reads WHITE like the hair it
                // matches, not gray.
                if (layer.Kind == SurfaceGeneratorKind.Fur)
                    albedo *= 0.65f + 0.45f * heightV;
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

        // Low-frequency tonal jitter around the base color; markings add the highlight on top.
        var mix     = ProceduralFields.Fbm3(layer.Seed + 7777, pos * (k * 0.15f), 2);
        var albedoT = (mix - 0.5f) * 2f * layer.ColorVariation;

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
    /// Scale plates: cellular noise in the flow-aligned flat coordinates — cells stretch
    /// along the flow by the elongation factor, so plates lie like they grew with the body.
    /// Each plate is a beveled plateau (height from the distance to the cell border) with
    /// its own color. Plates never CROSS-FADE between charts (blending two cell patterns
    /// ghosts them into smeared ridges) — the caller picks one chart and sinks the switch
    /// line into a crevice instead.
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
        var albedoT = (cellT - 0.5f) * 2f * layer.ColorVariation;

        return (height, albedoT, 1f);
    }

    /// <summary>
    /// Fur, built the way painted animal fur reads: strands GROUP into clumps (elongated
    /// cellular cells along the flow) separated by dark creases, and each strand is a sharp
    /// ridged-noise line. The coat wears the MAIN color throughout (dark roots, full color
    /// at the crests); the highlight color enters only through the markings, added by the
    /// caller. Flecks add sparse lighter tips.
    /// </summary>
    private static (float Height, float AlbedoT, float Coverage) EvaluateFur(
        ProceduralSurfaceLayer layer, Vector2 coord, float island, float k)
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

        // The coat wears the main (hair) color; markings (added by the caller) paint the
        // highlight over it. Per-clump tone jitter feeds the shared variation slider.
        var albedoT = (clumpTone - 0.5f) * 2f * layer.ColorVariation * 0.35f;

        // Sparse brighter flecks, elongated along the flow — stray hairs catching the light.
        if (layer.SpeckDensity > 0f)
        {
            var speck = ProceduralFields.Worley(layer.Seed + 4242, new Vector2(a * 2.2f, along * 0.5f));
            var fleck = 1f - ProceduralFields.Smooth(0.04f, 0.16f, speck.F1);
            var gate  = ProceduralFields.Hash01(layer.Seed + 555, (int)speck.CellHash, 0, 0) < layer.SpeckDensity ? 1f : 0f;
            albedoT += fleck * gate * 0.5f;
            height   = Math.Clamp(height + fleck * gate * 0.15f, 0f, 1f);
        }

        // A hint of skin in the deepest clump separations — the coat itself stays opaque
        // like a real pelt (letting more skin through muddied white fur to gray). Opacity
        // remains the master control for a sparser coat.
        var gap      = 1f - separation;
        var coverage = 1f - gap * gap * gap * 0.25f;

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

    /// <summary>
    /// Custom markings: a user-imported tileable image sampled triplanar — three axis-aligned
    /// world-space projections blended by the surface normal — so the pattern keeps its aspect
    /// on every surface orientation (a single cylinder projection stretched wherever the
    /// surface turns horizontal: shoulders, chest, around the limbs) and stays continuous
    /// across canvases. Sharpened weights keep the blend bands narrow, and the threshold below
    /// re-sharpens the shapes they soften.
    /// </summary>
    private static float EvaluateCustomMarkings(ProceduralSurfaceLayer layer, MarkingPatternImage? pattern,
        Vector3 pos, Vector3 normal)
    {
        if (pattern == null || layer.MarkingAmount <= 0f)
            return 0f;

        var tile = MathF.Max(0.005f, layer.MarkingScaleCm / 100f);

        var w = new Vector3(MathF.Abs(normal.X), MathF.Abs(normal.Y), MathF.Abs(normal.Z));
        w *= w;
        w *= w;
        var sum = w.X + w.Y + w.Z;
        if (sum <= 1e-6f)
        {
            w   = Vector3.UnitY;
            sum = 1f;
        }

        var value = (w.X * SampleWrapped(pattern, pos.Z / tile, -pos.Y / tile)
          + w.Y * SampleWrapped(pattern, pos.X / tile, pos.Z / tile)
          + w.Z * SampleWrapped(pattern, pos.X / tile, -pos.Y / tile)) / sum;

        var cut = 1f - Math.Clamp(layer.MarkingAmount, 0f, 1f);
        return ProceduralFields.Smooth(cut - 0.08f, cut + 0.08f, value);
    }

    /// <summary> Bilinear sample with wrap on both axes — the pattern tiles over the unbounded frame. </summary>
    private static float SampleWrapped(MarkingPatternImage p, float u, float v)
    {
        static int Mod(int a, int m)
            => (a % m + m) % m;

        var fx = u * p.Width - 0.5f;
        var fy = v * p.Height - 0.5f;
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;
        var x1 = Mod(x0 + 1, p.Width);
        var y1 = Mod(y0 + 1, p.Height);
        x0 = Mod(x0, p.Width);
        y0 = Mod(y0, p.Height);

        var top    = p.Intensity[y0 * p.Width + x0] + (p.Intensity[y0 * p.Width + x1] - p.Intensity[y0 * p.Width + x0]) * tx;
        var bottom = p.Intensity[y1 * p.Width + x0] + (p.Intensity[y1 * p.Width + x1] - p.Intensity[y1 * p.Width + x0]) * tx;
        return top + (bottom - top) * ty;
    }

    private static float ApplyContrast(float v, float contrast)
        => Math.Clamp(0.5f + (v - 0.5f) * (contrast * 2f), 0f, 1f);

    private static Vector3 Unpack(uint packed)
    {
        var c = new Rgba32(packed);
        return new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
    }

    private static byte ToByte(float v)
        => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
