using System;
using DynamicTextureManager.DTextures.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Whole-canvas hair adjustments, applied in place at the source resolution: the shine
/// adjustment scales the hair mask's surface channels. (The highlight-editing pipeline that
/// used to live here — procedural placement, then brush painting — was removed after it never
/// produced natural results; highlight areas are now consumed as-is by the animated-highlight
/// conversion, see <see cref="AnimatedHairBuilder"/>.)
/// </summary>
public static class HairAdjust
{
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
}
