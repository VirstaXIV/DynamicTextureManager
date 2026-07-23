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
    /// <summary>
    /// Extract at most <paramref name="maxColors"/> colors from the pixels whose alpha passes
    /// the layer's threshold. Images that already use few enough distinct colors keep them
    /// exactly; otherwise similar colors are merged by a Wu quantizer. The palette is sorted
    /// by luminance (brightest first) so re-extracting the same image is stable.
    /// </summary>
    public static uint[] ExtractPalette(string pngPath, int maxColors, float alphaThreshold)
    {
        maxColors = Math.Max(1, maxColors);
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

        if (opaque.Count == 0)
            return [];

        var distinct = opaque.Distinct().ToList();
        if (distinct.Count > maxColors)
            distinct = QuantizeDown(opaque, maxColors);

        return distinct
            .OrderByDescending(Luminance)
            .ThenBy(c => c.PackedValue)
            .Select(c => c.PackedValue)
            .Distinct()
            .ToArray();
    }

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
    /// The composite-time recolor for diffuse-target decals: the pixel's RGB is replaced by
    /// the tint color of its nearest palette slot, its alpha kept — so opacity still fades
    /// the whole decal after tinting. No-op unless the layer's tint is active and consistent.
    /// </summary>
    public static Rgba32 ApplyTint(in Rgba32 sample, DecalLayer layer)
    {
        if (!layer.HasTint)
            return sample;

        var tint = new Rgba32(layer.TintColors[NearestIndex(sample, layer.PaletteColors)]);
        return new Rgba32(tint.R, tint.G, tint.B, sample.A);
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
