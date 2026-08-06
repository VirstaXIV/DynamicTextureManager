using System;
using System.Numerics;
using DynamicTextureManager.DTextures.Data;
using ImSharp;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace DynamicTextureManager.UI.Panels;

/// <summary>
/// Editor for one procedural surface layer (fur / scales / skin pattern). Owned by DecalsTab,
/// which draws it inside the layer list and saves on any reported change. The essentials sit
/// on top; everything situational folds into Fine-Tuning so the common flow stays short.
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

    /// <summary>
    /// Draw the layer's settings; returns true when anything changed.
    /// <paramref name="bodyRegions"/> shows the per-body-part coverage sliders — only
    /// meaningful on the merged body canvas, whose triangles carry model units.
    /// </summary>
    public bool Draw(ProceduralSurfaceLayer layer, bool bodyRegions)
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

        var characterColors = layer.UseCharacterColors;
        if (Im.Checkbox("Use Hair Colors"u8, ref characterColors))
        {
            layer.UseCharacterColors = characterColors;
            changed                  = true;
        }

        Im.Tooltip.OnHover("Colors follow your character: hair color as the base, highlights on the crests. Untick to pick your own."u8);

        if (!layer.UseCharacterColors)
        {
            changed |= DrawPackedColor("Color A"u8, ref layer.ColorA);
            changed |= DrawPackedColor("Color B"u8, ref layer.ColorB);
        }

        changed |= DrawSliderClamped("Size (cm)"u8, ref layer.FeatureSizeCm, 0.2f, 10f, "%.1f"u8);
        Im.Tooltip.OnHover("Size of the plates, stripes or fur clumps on the body."u8);

        changed |= DrawSliderClamped("Opacity"u8, ref layer.Opacity, 0f, 1f);

        if (layer.Kind == SurfaceGeneratorKind.Pattern)
        {
            changed |= DrawPatternStyle(layer);
            changed |= DrawSliderClamped("Amount"u8, ref layer.Threshold, 0.1f, 0.9f);
            Im.Tooltip.OnHover("How much of the skin the pattern covers."u8);
        }

        if (Im.Tree.Header("Fine-Tuning"u8))
        {
            using var indent = Im.Indent();

            var seed = layer.Seed;
            Im.Item.SetNextWidthScaled(220);
            if (Im.Slider("Variation"u8, ref seed, "%d"u8, 1, 999))
            {
                layer.Seed = Math.Clamp(seed, 1, 999);
                changed    = true;
            }

            Im.Tooltip.OnHover("Reshuffles the pattern without changing its look."u8);

            switch (layer.Kind)
            {
                case SurfaceGeneratorKind.Pattern:
                    changed |= DrawSliderClamped("Irregularity"u8, ref layer.WarpStrength, 0f, 1f);
                    break;
                case SurfaceGeneratorKind.Scales:
                    changed |= DrawSliderClamped("Elongation"u8, ref layer.ScaleElongation, 0.5f, 4f);
                    Im.Tooltip.OnHover("Stretches the plates along the flow direction."u8);
                    changed |= DrawSliderClamped("Bevel"u8, ref layer.BevelWidth, 0.05f, 1f);
                    Im.Tooltip.OnHover("Width of the rounded rim around each plate."u8);
                    break;
                case SurfaceGeneratorKind.Fur:
                    changed |= DrawSliderClamped("Strand Fineness"u8, ref layer.StrandAspect, 2f, 24f);
                    changed |= DrawSliderClamped("Curl"u8, ref layer.Curl, 0f, 1f);
                    changed |= DrawSliderClamped("Flecks"u8, ref layer.SpeckDensity, 0f, 1f);
                    Im.Tooltip.OnHover("Sparse lighter hairs catching the light."u8);
                    break;
            }

            changed |= DrawSliderClamped("Color Variation"u8, ref layer.ColorVariation, 0f, 1f);
            changed |= DrawSliderClamped("Contrast"u8, ref layer.Contrast, 0f, 1f);
            changed |= DrawSliderClamped("Shading"u8, ref layer.CavityAmount, 0f, 1f);
            Im.Tooltip.OnHover("Darkens the crevices of the pattern."u8);
            changed |= DrawSliderClamped("Relief"u8, ref layer.HeightStrength, 0f, 1f);
            Im.Tooltip.OnHover("Bakes the pattern's depth into the normal map so light catches it."u8);
            changed |= DrawSliderClamped("Roughness"u8, ref layer.RoughnessAmount, -1f, 1f);
            Im.Tooltip.OnHover("Shifts the surface finish — negative is glossier, positive more matte. Only visible in-game."u8);

            if (bodyRegions)
            {
                changed |= DrawSliderClamped("Face Coverage"u8, ref layer.WeightFace, 0f, 1f);
                Im.Tooltip.OnHover("The face is its own canvas — this fades its pattern; the brush paints the body."u8);
            }
        }

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

    private static bool DrawPatternStyle(ProceduralSurfaceLayer layer)
    {
        var changed = false;

        Im.Item.SetNextWidthScaled(220);
        using var combo = Im.Combo.Begin("Style"u8, PatternLabel(layer.PatternStyle));
        if (combo)
            foreach (var style in PatternStyles)
            {
                if (!Im.Selectable(PatternLabel(style), style == layer.PatternStyle) || style == layer.PatternStyle)
                    continue;

                layer.PatternStyle = style;
                changed            = true;
            }

        return changed;
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

    private static bool DrawSliderClamped(ReadOnlySpan<byte> label, ref float value, float min, float max,
        ReadOnlySpan<byte> format = default)
    {
        Im.Item.SetNextWidthScaled(220);
        var v = value;
        if (!Im.Slider(label, ref v, format.IsEmpty ? "%.2f"u8 : format, min, max))
            return false;

        value = Math.Clamp(v, min, max);
        return true;
    }
}
