using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration.Shaders;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

// Stage C of the procedural surface bake: compose the cached generation into the
// requested output — diffuse colors, or relief/finish for sibling normal/mask textures.
public static partial class ProceduralSurfaceBaker
{
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
        // Depth in ABSOLUTE millimeters (up to ~8 mm at full strength), independent of the
        // feature size — tying it to the feature size made fine fur (small Size values)
        // physically incapable of visible relief. BC7 and the shader both soften the
        // result — authored deliberately hot.
        var amplitude = Math.Clamp(layer.HeightStrength, 0f, 1f) * 0.008f;
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

    private static byte ShadedBlend(byte baseValue, uint layerValue, float alpha, float shade)
        => (byte)Math.Clamp((int)MathF.Round((baseValue + ((float)layerValue - baseValue) * alpha) * shade), 0, 255);

    private static byte LerpByte(byte from, byte to, float t)
        => (byte)Math.Clamp((int)Math.Round(from + (to - from) * t), 0, 255);
}
