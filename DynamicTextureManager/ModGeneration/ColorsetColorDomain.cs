using System;
using System.Collections.Generic;
using System.Numerics;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Interop;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Colorset colors live in the game's SQUARED domain: the shader's display response is
/// ~sqrt of the stored value (the same convention as the customize colors — see the
/// <see cref="PackSqrt"/> pattern). Row edits store the squared value so authored-row
/// roundtrips stay byte-exact (extraction); every picker and palette boundary converts
/// through these, so the color the user picks is the color the game actually renders.
/// Without this, decal colors written as display values rendered washed out in game
/// (sqrt-brightened) while the preview showed them as picked.
/// </summary>
public static class ColorsetColorDomain
{
    /// <summary> Darkening applied to a shade-partner row: the benign blend target for a pair's unused half. </summary>
    public const float ShadeFactor = 0.6f;

    public static float[] DisplayToRowRgb(float r, float g, float b)
        => [r * r, g * g, b * b];

    public static Vector3 RowToDisplayRgb(IReadOnlyList<float> rgb)
        => new(MathF.Sqrt(MathF.Max(0f, rgb[0])), MathF.Sqrt(MathF.Max(0f, rgb[1])), MathF.Sqrt(MathF.Max(0f, rgb[2])));

    /// <summary> A row edit's diffuse packed as a display-domain Rgba32 (for presets/swatches). </summary>
    public static uint PackedDisplayDiffuse(ColorRowEdit row)
    {
        var display = RowToDisplayRgb(row.Diffuse);
        return new Rgba32(display.X, display.Y, display.Z).PackedValue;
    }

    public static uint PackSqrt(float[] rgb, float scale)
        => new Rgba32(
            MathF.Sqrt(Math.Clamp(rgb[0] * scale, 0f, 1f)),
            MathF.Sqrt(Math.Clamp(rgb[1] * scale, 0f, 1f)),
            MathF.Sqrt(Math.Clamp(rgb[2] * scale, 0f, 1f))).PackedValue;

    /// <summary>
    /// The animated conversion's hair + highlight colors as they will bake: the character's
    /// live colors (squared — colorset colors live in the squared domain) unless the override
    /// toggle is set; stored values also serve as the fallback while the character is
    /// unreadable. The effect color is always the stored one and not part of this.
    /// </summary>
    public static (float[] Base, float[] Highlight) EffectiveAnimatedColors(AnimatedHairEdit edit, HairColors? live)
    {
        if (edit.OverrideHairColors || live is not { } colors)
            return (edit.BaseColor, edit.HighlightColor);

        return ([colors.Main.X * colors.Main.X, colors.Main.Y * colors.Main.Y, colors.Main.Z * colors.Main.Z],
            [colors.Highlight.X * colors.Highlight.X, colors.Highlight.Y * colors.Highlight.Y, colors.Highlight.Z * colors.Highlight.Z]);
    }
}
