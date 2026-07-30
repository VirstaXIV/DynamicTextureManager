using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using DynamicTextureManager.DTextures.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Whole-canvas hair adjustments, applied in place at the source resolution. The highlight
/// adjustment rewrites only the hair normal's blue channel (the main-color/highlight blend);
/// the shine adjustment scales the hair mask's channels. See <see cref="ProceduralMasks"/>
/// for the deterministic noise/gradient sources.
/// </summary>
public static class HairAdjust
{
    /// <summary>
    /// Modulate the highlight-blend channel. Everything the tool GENERATES is anchored to the
    /// hairstyle's own authored highlights so it can never take over harshly:
    /// - the generated intensity is capped at the authored channel's measured strength
    ///   (<see cref="AuthoredIntensity"/>) instead of painting flat full-blend values;
    /// - strand variation multiplies in per-strand intensity jitter and ragged along-strand
    ///   breakup, so generated areas read as layered hair rather than solid paint;
    /// - the final Strength fades between the authored channel and the adjusted result.
    /// The along-strand coordinate comes from GEOMETRY (<see cref="HairGeoMap"/>): the
    /// normalized distance from the skull — continuous across every UV piece, immune to the
    /// flipped and split strand layouts hair textures use. Strand IDENTITY (which strand a
    /// texel belongs to) is the texel's position ACROSS the strand flow of its texture piece,
    /// where the flow direction is read from the ARTWORK itself (<see cref="ArtFlow"/>): the
    /// original texture indicates how the hair actually runs in each piece, so patterns follow
    /// rotated pieces instead of assuming strands lie vertical in the texture. Without mesh
    /// geometry, absolute texture coordinates remain as a fallback.
    /// </summary>
    public static void ApplyHighlight(Image<Rgba32> image, HairHighlightLayer layer, MaterialMesh? mesh = null)
    {
        if (layer.IsNeutral)
            return;

        var geo   = mesh == null ? null : HairGeoMap.Get(mesh);
        var flows = geo == null || mesh == null ? null : GetArtFlow(mesh, geo, image);

        var strength         = Math.Clamp(layer.Strength, 0f, 1f);
        var variation        = Math.Clamp(layer.StrandVariation, 0f, 1f);
        var gradientStrength = Math.Clamp(layer.GradientStrength, 0f, 1f);
        var elongation       = MathF.Max(1f, layer.Elongation);
        // fBm values cluster around 0.5, so the cut runs 0.85 (coverage 0 — nothing passes)
        // down to 0.15 (coverage 1 — nearly everything) to spread the slider's useful range.
        var threshold = 0.5f + (0.5f - Math.Clamp(layer.Coverage, 0f, 1f)) * 0.7f;
        var softness  = MathF.Max(0.005f, layer.Softness * 0.5f);
        var width     = image.Width;
        var height    = image.Height;

        // The dominant strand-identity term is constant along the strand, so a lit strand runs
        // its whole length; the along-strand term modulates it into ragged runs.
        var columnWeight = 1f - 1f / elongation;
        // The intensity envelope of the style's own highlights — generated values scale to it.
        var authoredScale = AuthoredIntensity(image);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; ++y)
            {
                var row = accessor.GetRowSpan(y);
                var v   = (y + 0.5f) / height;
                for (var x = 0; x < width; ++x)
                {
                    var authored = row[x].B / 255f;
                    var u        = (x + 0.5f) / width;

                    // Along-strand position (0 roots .. 1 tips) and the piece's art flow.
                    float strandD;
                    var   flow = new Vector2(0f, 1f);
                    if (geo == null || !geo.TryGet(x, y, width, height, out strandD, out var islandId))
                        strandD = v;
                    else if (flows != null)
                        flow = flows[islandId];

                    // "Which strand": the texel's position across the piece's flow (projection
                    // onto the flow's perpendicular) — constant along a strand drawn in the
                    // piece's direction, so the pattern follows the art.
                    var cross     = u * -flow.Y + v * flow.X;
                    var cellCoord = new Vector2(cross * layer.NoiseScale, 0f);

                    // Zone boundaries are perturbed per texel by fine strand-scale noise mixed
                    // with the normal map's own strand detail, so a dye line shreds into
                    // strand-following wisps instead of a flat geometric cut. The noise term
                    // keeps the wisps fine even when a mod's normal has broad painterly detail.
                    var wispNoise = ProceduralMasks.Fbm(layer.Seed + 53,
                        new Vector2(cross * layer.NoiseScale * 2f, strandD * 12f), 2);
                    var wisp = ((wispNoise - 0.5f) + (row[x].G - 128) / 255f) * layer.BaseFeather * 0.6f;

                    // Placement: where the highlights sit, scaled to the authored intensity.
                    float value;
                    switch (layer.Base)
                    {
                        case HighlightBase.Inverted:
                            value = (1f - Math.Clamp(authored / authoredScale, 0f, 1f)) * authoredScale;
                            break;
                        case HighlightBase.Roots:
                            value = Zone(strandD + wisp, layer.BaseExtent, layer.BaseFeather) * authoredScale;
                            break;
                        case HighlightBase.Tips:
                            value = Zone(1f - strandD + wisp, layer.BaseExtent, layer.BaseFeather) * authoredScale;
                            break;
                        case HighlightBase.MainOnly:
                            value = 0f;
                            break;
                        case HighlightBase.HighlightOnly:
                            value = authoredScale;
                            break;
                        case HighlightBase.StrandsAdd:
                        case HighlightBase.StrandsOnly:
                        {
                            var cell = ProceduralMasks.Fbm(layer.Seed + 7919, cellCoord, 2);
                            var wave = ProceduralMasks.Fbm(layer.Seed,
                                new Vector2(cross * layer.NoiseScale, strandD * layer.NoiseScale / elongation), layer.Octaves);
                            var strand = wave + (cell - wave) * columnWeight;
                            var t      = Math.Clamp((strand - (threshold - softness)) / (2f * softness), 0f, 1f);
                            value = t * t * (3f - 2f * t) * authoredScale;
                            break;
                        }
                        default:
                            value = authored;
                            break;
                    }

                    // Strand variation: per-strand intensity jitter plus ragged along-strand
                    // breakup — applied to generated AND authored placements alike, so even
                    // the plain layout can be naturalized strand-by-strand.
                    if (variation > 0f && layer.Base is not HighlightBase.MainOnly)
                    {
                        var jitter = 0.55f + ProceduralMasks.ValueNoise(layer.Seed + 31337, cellCoord) * 0.7f;
                        var ragged = ProceduralMasks.Fbm(layer.Seed + 4241,
                            new Vector2(cross * layer.NoiseScale, strandD * 6f), 2);
                        var factor = (1f + (jitter - 1f) * variation)
                          * (1f + (ragged * 1.5f - 1f) * variation * 0.5f);
                        value *= MathF.Max(0f, factor);
                    }

                    // Added strands never dim the authored highlights beneath them.
                    if (layer.Base is HighlightBase.StrandsAdd)
                        value = MathF.Max(authored, value);

                    if (layer.GradientEnabled)
                    {
                        // The gradient's vertical component runs along the strands (root→tip).
                        var gradient = ProceduralMasks.Gradient(new Vector2(u, strandD), layer.GradientAngleDeg,
                            layer.GradientStart, layer.GradientEnd, layer.GradientInvert);
                        value *= 1f + (gradient - 1f) * gradientStrength;
                    }

                    value = Math.Clamp(layer.Contrast * (value - 0.5f) + 0.5f + layer.Bias, 0f, 1f);

                    row[x].B = (byte)Math.Clamp((int)MathF.Round((authored + (value - authored) * strength) * 255f), 0, 255);
                }
            }
        });
    }

    private static readonly ConditionalWeakTable<MaterialMesh, Vector2[]> ArtFlowCache = new();

    /// <summary>
    /// The strand-flow direction of each texture piece, read from the ARTWORK: the structure
    /// tensor of the normal map's strand detail (G channel) accumulated per piece. Strand art
    /// draws edges ALONG the strands, so the dominant gradient runs across them — the flow is
    /// its perpendicular. The geometry's root→tip polarity picks which end is which; pieces
    /// with too little directional detail fall back to the polarity direction. Cached per mesh
    /// (the analysis runs on the pristine base, which is constant for a material).
    /// </summary>
    private static Vector2[] GetArtFlow(MaterialMesh mesh, HairGeoMap geo, Image<Rgba32> image)
    {
        lock (ArtFlowCache)
        {
            if (ArtFlowCache.TryGetValue(mesh, out var cached))
                return cached;
        }

        var width  = image.Width;
        var height = image.Height;
        var g      = new byte[width * height];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; ++y)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; ++x)
                    g[y * width + x] = row[x].G;
            }
        });

        var sxx = new double[geo.IslandCount];
        var sxy = new double[geo.IslandCount];
        var syy = new double[geo.IslandCount];
        for (var y = 1; y < height - 1; y += 2)
        for (var x = 1; x < width - 1; x += 2)
        {
            if (!geo.TryGet(x, y, width, height, out _, out var islandId))
                continue;

            float gx = g[y * width + x + 1] - g[y * width + x - 1];
            float gy = g[(y + 1) * width + x] - g[(y - 1) * width + x];
            sxx[islandId] += gx * gx;
            sxy[islandId] += gx * gy;
            syy[islandId] += gy * gy;
        }

        var flows = new Vector2[geo.IslandCount];
        for (var i = 0; i < flows.Length; ++i)
        {
            var polarity  = geo.Polarity(i);
            var total     = sxx[i] + syy[i];
            var coherence = total < 1e-3
                ? 0.0
                : Math.Sqrt((sxx[i] - syy[i]) * (sxx[i] - syy[i]) + 4.0 * sxy[i] * sxy[i]) / total;
            if (coherence < 0.1)
            {
                flows[i] = polarity;
                continue;
            }

            // Dominant gradient orientation; the strands run perpendicular to it.
            var theta = 0.5 * Math.Atan2(2.0 * sxy[i], sxx[i] - syy[i]);
            var flow  = new Vector2(-(float)Math.Sin(theta), (float)Math.Cos(theta));
            if (Vector2.Dot(flow, polarity) < 0f)
                flow = -flow;

            flows[i] = flow;
        }

        lock (ArtFlowCache)
        {
            if (!ArtFlowCache.TryGetValue(mesh, out var cached))
                ArtFlowCache.Add(mesh, cached = flows);
            return cached;
        }
    }

    /// <summary>
    /// The intensity envelope of the style's own highlights: the 90th-percentile blue-channel
    /// value over visibly highlighted texels (sampled sparsely). Styles authored with barely
    /// any highlights fall back to a moderate envelope so generated placements stay visible.
    /// </summary>
    private static float AuthoredIntensity(Image<Rgba32> image)
    {
        var histogram = new int[256];
        long count = 0, sampled = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y += 4)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x += 4)
                {
                    ++sampled;
                    if (row[x].A > 32 && row[x].B > 12)
                    {
                        ++histogram[row[x].B];
                        ++count;
                    }
                }
            }
        });

        if (count < Math.Max(64, sampled / 500))
            return 0.7f;

        var target  = count * 9 / 10;
        long running = 0;
        for (var i = 0; i < 256; ++i)
        {
            running += histogram[i];
            if (running >= target)
                return Math.Clamp(i / 255f, 0.25f, 1f);
        }

        return 0.7f;
    }

    /// <summary>
    /// Scale the hair mask's surface channels: R specular power, G roughness (scale plus
    /// offset), B subsurface thickness, A ambient occlusion.
    /// </summary>
    public static void ApplyShine(Image<Rgba32> image, HairShineLayer layer)
    {
        if (layer.IsNeutral)
            return;

        var roughnessOffset = layer.RoughnessOffset * 255f;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; ++y)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; ++x)
                {
                    row[x].R = ScaleByte(row[x].R, layer.SpecScale);
                    row[x].G = (byte)Math.Clamp((int)MathF.Round(row[x].G * layer.RoughnessScale + roughnessOffset), 0, 255);
                    row[x].B = ScaleByte(row[x].B, layer.SssScale);
                    row[x].A = ScaleByte(row[x].A, layer.AoScale);
                }
            }
        });
    }

    private static byte ScaleByte(byte value, float scale)
        => (byte)Math.Clamp((int)MathF.Round(value * scale), 0, 255);

    /// <summary> 1 inside the first <paramref name="extent"/> of the coordinate, feathered over <paramref name="feather"/>. </summary>
    private static float Zone(float coordinate, float extent, float feather)
    {
        var t = Math.Clamp((extent - coordinate) / MathF.Max(0.001f, feather) + 0.5f, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
