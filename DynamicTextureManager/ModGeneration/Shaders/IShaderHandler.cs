using System.Collections.Generic;
using Penumbra.GameData.Files;

namespace DynamicTextureManager.ModGeneration.Shaders;

public enum TextureSlot
{
    Diffuse,
    Normal,
    Mask,
    Index,
    Specular,
    Unknown,
}

public readonly record struct TextureSlotInfo(string GamePath, TextureSlot Slot, bool SupportsDecals);

/// <summary>
/// How a material takes decals and color edits — the UI adapts its options to this.
/// ModernColorset gear claims colorset rows; LegacyDiffuse and Skin bake decal colors
/// directly into the diffuse texture (skin decals behave like tattoos).
/// </summary>
public enum MaterialKind
{
    ModernColorset,
    LegacyDiffuse,
    Skin,
    Unknown,
}

/// <summary>
/// Per-shader-package handling of materials: what can be edited and how its textures are
/// classified. New shader types are supported by adding another handler to the registry.
/// </summary>
public interface IShaderHandler
{
    bool Matches(string shpkName);

    bool SupportsColorSet(MtrlFile material);

    /// <summary>
    /// Whether ID-map colorset decals (multi-color row remapping) work on this material.
    /// Only Dawntrail gear materials with the 32-row color table are supported; legacy
    /// gear, skin, hair etc. need their own flows and are gated off until those exist.
    /// </summary>
    bool SupportsColorsetDecals(MtrlFile material);

    bool SupportsDecals { get; }

    /// <summary> The material's editing family; drives which options the UI offers. </summary>
    MaterialKind Kind(MtrlFile material);

    IReadOnlyList<TextureSlotInfo> ClassifyTextures(MtrlFile material);
}
