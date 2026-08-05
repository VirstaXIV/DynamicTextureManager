using System;
using System.Numerics;
using DynamicTextureManager.DTextures.Data;
using ImSharp;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace DynamicTextureManager.UI.Panels;

/// <summary>
/// Editor for one procedural surface layer (fur / scales / skin pattern). Owned by DecalsTab,
/// which draws it inside the layer list and saves on any reported change.
/// </summary>
public sealed class ProceduralSurfaceSection
{
    public static string KindLabel(SurfaceGeneratorKind kind)
        => kind switch
        {
            SurfaceGeneratorKind.Scales => "Scales",
            SurfaceGeneratorKind.Fur    => "Fur",
            _                           => "Skin Pattern",
        };

    private static readonly SurfaceGeneratorKind[] Kinds =
        [SurfaceGeneratorKind.Pattern, SurfaceGeneratorKind.Scales, SurfaceGeneratorKind.Fur];

    /// <summary> Draw the layer's settings; returns true when anything changed. </summary>
    public bool Draw(ProceduralSurfaceLayer layer)
    {
        var changed = false;

        Im.Item.SetNextWidthScaled(220);
        using (var combo = Im.Combo.Begin("Type"u8, KindLabel(layer.Kind)))
        {
            if (combo)
                foreach (var kind in Kinds)
                {
                    if (!Im.Selectable(KindLabel(kind), kind == layer.Kind) || kind == layer.Kind)
                        continue;

                    layer.Kind = kind;
                    changed    = true;
                }
        }

        Im.Item.SetNextWidthScaled(220);
        var seed = layer.Seed;
        if (Im.Slider("Variation"u8, ref seed, "%d"u8, 1, 999))
        {
            layer.Seed = Math.Clamp(seed, 1, 999);
            changed    = true;
        }

        Im.Tooltip.OnHover("Reshuffles the pattern without changing its look."u8);

        changed |= DrawPackedColor("Color A"u8, ref layer.ColorA);
        changed |= DrawPackedColor("Color B"u8, ref layer.ColorB);

        Im.Item.SetNextWidthScaled(220);
        var size = layer.FeatureSizeCm;
        if (Im.Slider("Size (cm)"u8, ref size, "%.1f"u8, 0.2f, 10f))
        {
            layer.FeatureSizeCm = Math.Clamp(size, 0.2f, 10f);
            changed             = true;
        }

        Im.Item.SetNextWidthScaled(220);
        var opacity = layer.Opacity;
        if (Im.Slider("Opacity"u8, ref opacity, "%.2f"u8, 0f, 1f))
        {
            layer.Opacity = Math.Clamp(opacity, 0f, 1f);
            changed       = true;
        }

        if (layer.Kind == SurfaceGeneratorKind.Pattern)
            changed |= DrawPatternSettings(layer);

        changed |= DrawSliderClamped("Color Variation"u8, ref layer.ColorVariation, 0f, 1f);
        changed |= DrawSliderClamped("Contrast"u8, ref layer.Contrast, 0f, 1f);
        changed |= DrawSliderClamped("Shading"u8, ref layer.CavityAmount, 0f, 1f);
        Im.Tooltip.OnHover("Darkens the crevices of the pattern."u8);

        return changed;
    }

    private static readonly SurfacePatternStyle[] PatternStyles =
        [SurfacePatternStyle.Spots, SurfacePatternStyle.Stripes, SurfacePatternStyle.Marbling];

    private static string PatternLabel(SurfacePatternStyle style)
        => style switch
        {
            SurfacePatternStyle.Stripes  => "Stripes",
            SurfacePatternStyle.Marbling => "Marbling",
            _                            => "Spots",
        };

    private static bool DrawPatternSettings(ProceduralSurfaceLayer layer)
    {
        var changed = false;

        Im.Item.SetNextWidthScaled(220);
        using (var combo = Im.Combo.Begin("Style"u8, PatternLabel(layer.PatternStyle)))
        {
            if (combo)
                foreach (var style in PatternStyles)
                {
                    if (!Im.Selectable(PatternLabel(style), style == layer.PatternStyle) || style == layer.PatternStyle)
                        continue;

                    layer.PatternStyle = style;
                    changed            = true;
                }
        }

        changed |= DrawSliderClamped("Irregularity"u8, ref layer.WarpStrength, 0f, 1f);
        changed |= DrawSliderClamped("Amount"u8, ref layer.Threshold, 0.1f, 0.9f);
        Im.Tooltip.OnHover("How much of the skin the pattern covers."u8);

        return changed;
    }

    private static bool DrawSliderClamped(ReadOnlySpan<byte> label, ref float value, float min, float max)
    {
        Im.Item.SetNextWidthScaled(220);
        var v = value;
        if (!Im.Slider(label, ref v, "%.2f"u8, min, max))
            return false;

        value = Math.Clamp(v, min, max);
        return true;
    }

    private static bool DrawPackedColor(ReadOnlySpan<byte> label, ref uint packed)
    {
        var rgba  = new Rgba32(packed);
        var color = new Vector3(rgba.R / 255f, rgba.G / 255f, rgba.B / 255f);
        Im.Item.SetNextWidthScaled(220);
        if (!Im.Color.Editor(label, ref color, ColorEditorFlags.Float))
            return false;

        packed = new Rgba32(color.X, color.Y, color.Z).PackedValue;
        return true;
    }
}
