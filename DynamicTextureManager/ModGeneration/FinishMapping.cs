using System;
using DynamicTextureManager.DTextures.Data;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Single home for every empirical surface-finish constant, so one in-game verification
/// session can dial them in without hunting through the pipeline.
///
/// On Dawntrail colorset-driven gear, perceived shine is dominated by the colorset row's
/// Roughness/Specular; the mask map modulates on top (suspected: final roughness ≈
/// row.Roughness × mask roughness channel). Id-remap decals therefore drive BOTH the claimed
/// rows and the mask sibling; plain diffuse decals can only write the mask, so the authored
/// row values bound what they can achieve.
///
/// Mask channel semantics are still empirical (community docs: R ≈ cavity/spec occlusion,
/// G = roughness, B ≈ metalness/material influence). The channel index and inversion are
/// runtime-tunable through the debug section of the config window.
/// </summary>
public static class FinishMapping
{
    /// <summary> Mask channel that holds roughness (0=R 1=G 2=B), tunable for verification. </summary>
    public static int MaskRoughnessChannel = 1;

    /// <summary> Set if verification shows the mask channel stores gloss (1 - roughness). </summary>
    public static bool MaskInvertRoughness;

    /// <summary> Also scale the mask R channel by the spec multiplier — off until verified useful. </summary>
    public static bool MaskWriteSpec;

    /// <summary> Pull the tunables from the persisted configuration (startup and config-window edits). </summary>
    public static void Sync(Configuration config)
    {
        MaskRoughnessChannel = Math.Clamp(config.MaskRoughnessChannel, 0, 2);
        MaskInvertRoughness  = config.MaskInvertRoughness;
        MaskWriteSpec        = config.MaskWriteSpec;
    }

    /// <summary> Continuous values behind the Matte/Glossy presets. </summary>
    public static (float Roughness, float SpecScale) PresetValues(DecalFinishMode mode)
        => mode switch
        {
            DecalFinishMode.Matte  => (0.85f, 0.4f),
            DecalFinishMode.Glossy => (0.12f, 1.2f),
            _                      => (0.5f, 1f),
        };

    public static float EffectiveRoughness(DecalLayer layer)
        => layer.Finish == DecalFinishMode.Custom ? layer.FinishRoughness : PresetValues(layer.Finish).Roughness;

    public static float EffectiveSpecScale(DecalLayer layer)
        => layer.Finish == DecalFinishMode.Custom ? layer.FinishSpecScale : PresetValues(layer.Finish).SpecScale;

    public static byte MaskRoughnessByte(float roughness)
        => (byte)Math.Clamp((int)MathF.Round((MaskInvertRoughness ? 1f - roughness : roughness) * 255f), 0, 255);

    /// <summary> Write the finish into the mask pixel. </summary>
    public static void ApplyToMaskPixel(ref Rgba32 pixel, DecalLayer layer)
    {
        var rough = MaskRoughnessByte(EffectiveRoughness(layer));
        switch (MaskRoughnessChannel)
        {
            case 0:  pixel.R = rough; break;
            case 2:  pixel.B = rough; break;
            default: pixel.G = rough; break;
        }

        if (MaskWriteSpec)
            pixel.R = (byte)Math.Clamp((int)MathF.Round(pixel.R * EffectiveSpecScale(layer)), 0, 255);
    }

    /// <summary>
    /// Baseline dielectric specular the finish spec-scale multiplies. Absolute rather than
    /// template-relative: cloth templates often author specular 0, which would make Glossy a
    /// no-op, and metal templates author 1.0, which reads as chrome.
    /// </summary>
    public const float NeutralSpecular = 0.25f;

    /// <summary>
    /// Write the finish into a claimed colorset row. All writes are absolute, so repeated
    /// application is idempotent. A decal with an explicit finish is a dielectric print
    /// sitting on the surface, so the template row's other shine drivers are cleared:
    /// a built-mtrl dump showed rows seeded from an authored metal row kept Metalness=1,
    /// sheen ~1.4 and a sphere-map catchlight — metallic regardless of roughness. Callers
    /// must also rebase rows seeded from a metal template onto a dielectric one: metal rows
    /// carry BRDF scalars (s7=1/s11=0/tileAlpha=1 empirically) that disable the diffuse
    /// path, rendering white diffuse as dark grey once Metalness is cleared.
    /// </summary>
    public static void ApplyToRow(ColorRowEdit row, DecalLayer layer)
    {
        var spec = Math.Clamp(EffectiveSpecScale(layer) * NeutralSpecular, 0f, 1f);
        row.Roughness = EffectiveRoughness(layer);
        row.Specular  = [spec, spec, spec];

        row.Metalness      = 0f;
        row.SphereMapMask  = 0f;
        row.SphereMapIndex = 0;
        row.Anisotropy     = 0f;
        row.SheenRate      = 0f;
    }
}
