using System;
using System.Collections.Generic;
using System.Linq;
using DynamicTextureManager.DTextures.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Extracts a bounded color palette from a decal image for colorset decals. Extraction runs
/// only on explicit UI triggers and the result is stored on the layer; builds never
/// re-quantize, they only nearest-map pixels against the stored palette, which keeps
/// rebuilds deterministic.
/// </summary>
public static class DecalQuantizer
{
    /// <summary> Slot budget of a colorset material — no decal can ever use more colors than this. </summary>
    public const int MaxAutoColors = 12;

    /// <summary> Cap on the pixels the auto search quantizes and measures — strided, so deterministic. </summary>
    private const int SampleBudget = 120_000;

    /// <summary>
    /// Extract at most <paramref name="maxColors"/> colors from the pixels whose alpha passes
    /// the layer's threshold. Images that already use few enough distinct colors keep them
    /// exactly; otherwise similar colors are merged by a Wu quantizer. The palette is sorted
    /// by luminance (brightest first) so re-extracting the same image is stable.
    /// </summary>
    public static uint[] ExtractPalette(string pngPath, int maxColors, float alphaThreshold)
    {
        maxColors = Math.Max(1, maxColors);
        var opaque = LoadOpaquePixels(pngPath, alphaThreshold);
        if (opaque.Count == 0)
            return [];

        var distinct = opaque.Distinct().ToList();
        if (distinct.Count > maxColors)
            distinct = QuantizeDown(opaque, maxColors);

        return SortPalette(distinct);
    }

    /// <summary>
    /// Extract the SMALLEST palette that still renders the image faithfully: candidate
    /// palettes grow one color at a time until the blended rendering error — each pixel's
    /// distance to the nearest gradient-pair segment or solo color, the same model the
    /// composite renders with — drops within <paramref name="mergeDistance"/> (0-255 RGB
    /// scale, 95th percentile so stray specks don't force extra colors). The color count is
    /// automatic; the threshold is the only knob. Deterministic for the same image and
    /// settings, like <see cref="ExtractPalette"/>.
    /// </summary>
    public static uint[] ExtractPaletteAuto(string pngPath, float alphaThreshold, float mergeDistance)
    {
        var opaque = LoadOpaquePixels(pngPath, alphaThreshold);
        if (opaque.Count == 0)
            return [];

        // Strided subsample: plenty for a stable palette + error percentile, and it keeps
        // twelve quantizer passes over multi-megapixel decals instant.
        var stride = Math.Max(1, opaque.Count / SampleBudget);
        var sample = new List<Rgba32>((opaque.Count + stride - 1) / stride);
        for (var i = 0; i < opaque.Count; i += stride)
            sample.Add(opaque[i]);

        var distinct = sample.Distinct().ToList();
        var errors   = new float[sample.Count];

        List<Rgba32> best = distinct.Count <= MaxAutoColors
            ? distinct
            : RefineGradientEndpoints(sample, QuantizeDown(sample, MaxAutoColors));
        for (var count = 1; count < MaxAutoColors; ++count)
        {
            if (distinct.Count <= count)
                break; // the image has no more distinct colors than the candidate — exact already

            var candidate = RefineGradientEndpoints(sample, QuantizeDown(sample, count));
            if (RenderErrorP95(sample, candidate, errors) <= mergeDistance)
            {
                best = candidate;
                break;
            }
        }

        return SortPalette(best);
    }

    /// <summary>
    /// Stretch each gradient pair's two colors to the true ends of the ramp rendering
    /// through it. The Wu quantizer returns cluster CENTROIDS, but gradient rendering needs
    /// ENDPOINTS: a black-to-white ramp quantized to two colors yields dark/light gray, and
    /// the segment between those can never reach the ramp's extremes — the auto search would
    /// keep adding colors that a wider pair covers for free. Robust 1st/99th percentile of
    /// the member pixels' projections along the pair axis, so stray outliers don't stretch
    /// the pair. Solo colors stay centroids — they render as flat points either way.
    /// </summary>
    private static List<Rgba32> RefineGradientEndpoints(List<Rgba32> pixels, List<Rgba32> palette)
    {
        var packed = palette.Select(c => c.PackedValue).ToArray();
        var groups = ColorRowAllocator.GroupGradientPairs(packed);
        if (groups.All(g => g.Dark < 0))
            return palette;

        // One nearest-palette assignment pass shared by all groups.
        var assignment = new int[pixels.Count];
        for (var i = 0; i < pixels.Count; ++i)
            assignment[i] = NearestIndex(pixels[i], packed);

        var result  = new List<Rgba32>(palette);
        var scalars = new List<float>(pixels.Count);
        foreach (var group in groups)
        {
            if (group.Dark < 0)
                continue;

            var light = palette[group.Light];
            var dark  = palette[group.Dark];
            var ax    = light.R - (float)dark.R;
            var ay    = light.G - (float)dark.G;
            var az    = light.B - (float)dark.B;
            var len   = MathF.Sqrt(ax * ax + ay * ay + az * az);
            if (len < 1f)
                continue;

            ax /= len;
            ay /= len;
            az /= len;

            scalars.Clear();
            for (var i = 0; i < pixels.Count; ++i)
            {
                if (assignment[i] != group.Light && assignment[i] != group.Dark)
                    continue;

                var p = pixels[i];
                scalars.Add((p.R - dark.R) * ax + (p.G - dark.G) * ay + (p.B - dark.B) * az);
            }

            if (scalars.Count == 0)
                continue;

            scalars.Sort();
            var lo = scalars[(int)(0.01f * (scalars.Count - 1))];
            var hi = scalars[(int)(0.99f * (scalars.Count - 1))];

            Rgba32 At(float t) => new(
                (byte)Math.Clamp((int)MathF.Round(dark.R + ax * t), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(dark.G + ay * t), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(dark.B + az * t), 0, 255));

            result[group.Dark]  = At(lo);
            result[group.Light] = At(hi);
        }

        return result;
    }

    /// <summary>
    /// 95th-percentile distance from the pixels to what a palette can actually render: solo
    /// colors as points, gradient pairs (<see cref="ColorRowAllocator.GroupGradientPairs"/>)
    /// as the full segment between their colors — in-between pixels render blended, so they
    /// must not count as error.
    /// </summary>
    private static float RenderErrorP95(List<Rgba32> pixels, List<Rgba32> palette, float[] errors)
    {
        var packed = palette.Select(c => c.PackedValue).ToArray();
        var groups = ColorRowAllocator.GroupGradientPairs(packed);

        var segments = groups.Select(g =>
        {
            var a = palette[g.Light];
            var b = g.Dark >= 0 ? palette[g.Dark] : palette[g.Light];
            return (Ax: (float)a.R, Ay: (float)a.G, Az: (float)a.B,
                    Bx: (float)b.R, By: (float)b.G, Bz: (float)b.B);
        }).ToArray();

        for (var i = 0; i < pixels.Count; ++i)
        {
            var p    = pixels[i];
            var best = float.MaxValue;
            foreach (var s in segments)
            {
                var abx = s.Bx - s.Ax;
                var aby = s.By - s.Ay;
                var abz = s.Bz - s.Az;
                var lengthSq = abx * abx + aby * aby + abz * abz;
                var t = lengthSq <= 0f
                    ? 0f
                    : Math.Clamp(((p.R - s.Ax) * abx + (p.G - s.Ay) * aby + (p.B - s.Az) * abz) / lengthSq, 0f, 1f);
                var dx = p.R - (s.Ax + abx * t);
                var dy = p.G - (s.Ay + aby * t);
                var dz = p.B - (s.Az + abz * t);
                var dist = dx * dx + dy * dy + dz * dz;
                if (dist < best)
                    best = dist;
            }

            errors[i] = best;
        }

        var span = errors.AsSpan(0, pixels.Count);
        span.Sort();
        return MathF.Sqrt(span[(int)(0.95f * (span.Length - 1))]);
    }

    private static List<Rgba32> LoadOpaquePixels(string pngPath, float alphaThreshold)
    {
        var threshold = (byte)Math.Clamp((int)Math.Round(alphaThreshold * 255f), 1, 255);

        using var image = Image.Load<Rgba32>(pngPath);

        var opaque = new List<Rgba32>();
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; ++y)
            {
                foreach (ref var pixel in accessor.GetRowSpan(y))
                {
                    if (pixel.A >= threshold)
                        opaque.Add(new Rgba32(pixel.R, pixel.G, pixel.B));
                }
            }
        });

        return opaque;
    }

    private static uint[] SortPalette(List<Rgba32> colors)
        => colors
            .OrderByDescending(Luminance)
            .ThenBy(c => c.PackedValue)
            .Select(c => c.PackedValue)
            .Distinct()
            .ToArray();

    /// <summary> Index of the palette color closest to the pixel by squared RGB distance. </summary>
    public static int NearestIndex(Rgba32 pixel, IReadOnlyList<uint> palette)
    {
        var best     = 0;
        var bestDist = int.MaxValue;
        for (var i = 0; i < palette.Count; ++i)
        {
            var c    = new Rgba32(palette[i]);
            var dr   = pixel.R - c.R;
            var dg   = pixel.G - c.G;
            var db   = pixel.B - c.B;
            var dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                best     = i;
            }
        }

        return best;
    }

    /// <summary>
    /// The composite-time recolor for diffuse-target decals: the pixel maps onto the segment
    /// between its two nearest palette colors and renders the same blend of their tint
    /// colors, its alpha kept — so opacity still fades the whole decal after tinting.
    /// Blending instead of snapping to the single nearest slot preserves the decal's
    /// anti-aliasing and interior gradients, which otherwise posterize into flat regions at
    /// low Max Colors. No-op unless the layer's tint is active and consistent.
    /// </summary>
    public static Rgba32 ApplyTint(in Rgba32 sample, DecalLayer layer)
    {
        if (!layer.HasTint)
            return sample;

        var (near, far, t) = NearestBlend(sample, layer.PaletteColors);
        var tintNear       = new Rgba32(layer.TintColors[near]);
        var tintFar        = new Rgba32(layer.TintColors[far]);
        return new Rgba32(
            LerpByte(tintNear.R, tintFar.R, t),
            LerpByte(tintNear.G, tintFar.G, t),
            LerpByte(tintNear.B, tintFar.B, t),
            sample.A);
    }

    /// <summary>
    /// The two nearest palette slots to a pixel and where between them it sits: the pixel
    /// projected onto the segment from the nearest color toward the runner-up, clamped to
    /// [0, 1]. Exact palette hits return t = 0; a single-color palette never blends. The
    /// projection is symmetric around the midpoint, so neighboring pixels crossing the
    /// "which one is nearest" boundary blend continuously instead of seaming.
    /// </summary>
    public static (int Near, int Far, float T) NearestBlend(in Rgba32 pixel, IReadOnlyList<uint> palette)
    {
        if (palette.Count < 2)
            return (0, 0, 0f);

        var near     = 0;
        var far      = 0;
        var nearDist = int.MaxValue;
        var farDist  = int.MaxValue;
        for (var i = 0; i < palette.Count; ++i)
        {
            var c    = new Rgba32(palette[i]);
            var dr   = pixel.R - c.R;
            var dg   = pixel.G - c.G;
            var db   = pixel.B - c.B;
            var dist = dr * dr + dg * dg + db * db;
            if (dist < nearDist)
            {
                farDist  = nearDist;
                far      = near;
                nearDist = dist;
                near     = i;
            }
            else if (dist < farDist)
            {
                farDist = dist;
                far     = i;
            }
        }

        var a  = new Rgba32(palette[near]);
        var b  = new Rgba32(palette[far]);
        var ab = new [] { b.R - a.R, b.G - a.G, b.B - a.B };
        var lengthSq = ab[0] * ab[0] + ab[1] * ab[1] + ab[2] * ab[2];
        if (lengthSq == 0)
            return (near, far, 0f);

        var dot = (pixel.R - a.R) * ab[0] + (pixel.G - a.G) * ab[1] + (pixel.B - a.B) * ab[2];
        return (near, far, Math.Clamp(dot / (float)lengthSq, 0f, 1f));
    }

    private static byte LerpByte(byte from, byte to, float t)
        => (byte)Math.Clamp((int)Math.Round(from + (to - from) * t), 0, 255);

    /// <summary>
    /// For each palette index of a colorset decal, the palette index rendering on the other
    /// half of the same claimed pair (its gradient partner), or -1 when the color owns its
    /// pair alone. Computed once per bake/preview pass — the stamping loops are per-texel.
    /// </summary>
    public static int[] GradientPartners(DecalLayer layer)
    {
        var rows     = layer.PaletteRows;
        var partners = new int[rows.Count];
        Array.Fill(partners, -1);
        for (var i = 0; i < rows.Count; ++i)
        for (var j = 0; j < rows.Count; ++j)
        {
            if (i != j && rows[j] == (rows[i] ^ 1))
                partners[i] = j;
        }

        return partners;
    }

    /// <summary>
    /// The id-map G byte for a sample stamped into a gradient pair: where the pixel sits
    /// between the pair's B color (G = 0) and A color (G = 255), so the game's A/B
    /// interpolation reproduces the decal's own gradient and anti-aliasing per texel.
    /// </summary>
    public static byte GradientG(in Rgba32 sample, uint aColor, uint bColor)
    {
        var a  = new Rgba32(aColor);
        var b  = new Rgba32(bColor);
        var ba = new[] { a.R - b.R, a.G - b.G, a.B - b.B };
        var lengthSq = ba[0] * ba[0] + ba[1] * ba[1] + ba[2] * ba[2];
        if (lengthSq == 0)
            return 255;

        var dot = (sample.R - b.R) * ba[0] + (sample.G - b.G) * ba[1] + (sample.B - b.B) * ba[2];
        return (byte)Math.Clamp((int)Math.Round(255f * dot / lengthSq), 0, 255);
    }

    /// <summary>
    /// Merge similar colors with a Wu quantizer run over only the qualifying pixels, so
    /// transparent regions never pollute the palette.
    /// </summary>
    private static List<Rgba32> QuantizeDown(List<Rgba32> opaque, int maxColors)
    {
        // Pack the opaque pixels into a compact image for the quantizer.
        var width  = Math.Min(opaque.Count, 4096);
        var height = (opaque.Count + width - 1) / width;
        using var packed = new Image<Rgba32>(width, height);
        packed.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; ++y)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; ++x)
                {
                    var idx = y * width + x;
                    row[x] = idx < opaque.Count ? opaque[idx] : opaque[^1];
                }
            }
        });

        var quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = maxColors, Dither = null });
        using var frameQuantizer = quantizer.CreatePixelSpecificQuantizer<Rgba32>(SixLabors.ImageSharp.Configuration.Default);
        using var quantized      = frameQuantizer.BuildPaletteAndQuantizeFrame(packed.Frames.RootFrame, packed.Bounds);
        return quantized.Palette.ToArray().Select(c => new Rgba32(c.R, c.G, c.B)).Distinct().ToList();
    }

    private static float Luminance(Rgba32 c)
        => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
}
