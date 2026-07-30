using System;
using System.Collections.Generic;
using System.Linq;
using OtterGui.Services;
using Penumbra.GameData.Files;
using Penumbra.GameData.Files.MaterialStructs;
using Penumbra.GameData.Files.ShaderStructs;

namespace DynamicTextureManager.ModGeneration.Shaders;

public abstract class ShaderHandlerBase : IShaderHandler
{
    public abstract bool Matches(string shpkName);

    public virtual bool SupportsColorSet(MtrlFile material)
        => material.Table is ColorTable or LegacyColorTable;

    public virtual bool SupportsColorsetDecals(MtrlFile material)
        => false;

    public virtual bool SupportsDecals
        => true;

    public virtual MaterialKind Kind(MtrlFile material)
        => MaterialKind.Unknown;

    /// <summary> Whether the material binds a diffuse/base color texture. </summary>
    protected static bool HasDiffuse(MtrlFile material)
        => material.ShaderPackage.Samplers.Any(s => s.SamplerId == ShpkFile.DiffuseSamplerId && s.TextureIndex < material.Textures.Length);

    public virtual IReadOnlyList<TextureSlotInfo> ClassifyTextures(MtrlFile material)
    {
        var ret = new List<TextureSlotInfo>(material.Textures.Length);
        foreach (var sampler in material.ShaderPackage.Samplers)
        {
            if (sampler.TextureIndex >= material.Textures.Length)
                continue;

            var path = material.Textures[sampler.TextureIndex].Path;
            var slot = sampler.SamplerId switch
            {
                ShpkFile.DiffuseSamplerId  => TextureSlot.Diffuse,
                ShpkFile.NormalSamplerId   => TextureSlot.Normal,
                ShpkFile.MaskSamplerId     => TextureSlot.Mask,
                ShpkFile.IndexSamplerId    => TextureSlot.Index,
                ShpkFile.SpecularSamplerId => TextureSlot.Specular,
                _                          => TextureSlot.Unknown,
            };
            // Diffuse maps take color decals directly; index maps take colorset decals
            // (row remapping) only on materials whose shader/table combination supports them.
            var decals = SupportsDecals
             && slot switch
                {
                    TextureSlot.Diffuse => true,
                    TextureSlot.Index   => SupportsColorsetDecals(material),
                    _                   => false,
                };
            ret.Add(new TextureSlotInfo(path, slot, decals));
        }

        return ret;
    }
}

/// <summary> Dawntrail character shaders (gear): colorset editing and decals on the diffuse/base texture. </summary>
public sealed class CharacterShaderHandler : ShaderHandlerBase
{
    private static readonly string[] Names =
        ["character.shpk", "characterglass.shpk", "characterscroll.shpk", "characterinc.shpk", "charactertransparency.shpk", "characterstockings.shpk"];

    public override bool Matches(string shpkName)
        => Names.Contains(shpkName, StringComparer.OrdinalIgnoreCase);

    public override bool SupportsColorsetDecals(MtrlFile material)
        => material.Table is ColorTable;

    public override MaterialKind Kind(MtrlFile material)
        => material.Table is ColorTable ? MaterialKind.ModernColorset : MaterialKind.Unknown;
}

/// <summary>
/// Pre-Dawntrail character shader with the legacy 16-row table. The legacy colorset row is
/// selected through the normal map's alpha with fractional interpolation between adjacent
/// rows and its color is multiplied with the diffuse — there is no id-map pair scheme to
/// claim rows through, so decal colors are baked into the diffuse texture instead.
/// </summary>
public sealed class CharacterLegacyShaderHandler : ShaderHandlerBase
{
    public override bool Matches(string shpkName)
        => string.Equals(shpkName, "characterlegacy.shpk", StringComparison.OrdinalIgnoreCase);

    public override MaterialKind Kind(MtrlFile material)
        => HasDiffuse(material) ? MaterialKind.LegacyDiffuse : MaterialKind.Unknown;
}

/// <summary> Skin shader (body/face): tattoo-style decals baked into the diffuse texture, no colorset. </summary>
public sealed class SkinShaderHandler : ShaderHandlerBase
{
    public override bool Matches(string shpkName)
        => string.Equals(shpkName, "skin.shpk", StringComparison.OrdinalIgnoreCase);

    public override bool SupportsColorSet(MtrlFile material)
        => false;

    public override MaterialKind Kind(MtrlFile material)
        => MaterialKind.Skin;
}

/// <summary>
/// Hair shader: no diffuse and no colorset — the wearer's customize hair/highlight colors are
/// blended in-shader by the normal map's blue channel (0 = main color, 1 = highlight color), so
/// decals target the normal texture and stamp highlight patterns rather than colors. Materials
/// whose GetSubColor key selects the Face variant (brows/lashes) reinterpret that channel as the
/// race-feature color and are left unsupported.
/// </summary>
public sealed class HairShaderHandler : ShaderHandlerBase
{
    private static readonly uint SubColorKey  = new Name("GetSubColor").Crc32;
    private static readonly uint SubColorFace = new Name("GetSubColorFace").Crc32;

    public override bool Matches(string shpkName)
        => string.Equals(shpkName, "hair.shpk", StringComparison.OrdinalIgnoreCase);

    public override bool SupportsColorSet(MtrlFile material)
        => false;

    /// <summary> An absent GetSubColor key means the default Hair variant; only an explicit Face value gates off. </summary>
    public static bool IsFaceVariant(MtrlFile material)
        => material.ShaderPackage.ShaderKeys.Any(k => k.Key == SubColorKey && k.Value == SubColorFace);

    public override MaterialKind Kind(MtrlFile material)
        => IsFaceVariant(material) ? MaterialKind.Unknown : MaterialKind.Hair;

    public override IReadOnlyList<TextureSlotInfo> ClassifyTextures(MtrlFile material)
    {
        var ret = base.ClassifyTextures(material);
        return Kind(material) is MaterialKind.Hair
            ? ret.Select(i => i.Slot is TextureSlot.Normal ? i with { SupportsDecals = true } : i).ToList()
            : ret;
    }
}

/// <summary> Unknown shaders: expose the raw texture list, no colorset, decals only on decodable diffuse textures. </summary>
public sealed class FallbackShaderHandler : ShaderHandlerBase
{
    public override bool Matches(string shpkName)
        => true;

    public override bool SupportsColorSet(MtrlFile material)
        => false;
}

/// <summary> Picks the handler for a material; first match wins, the fallback always matches. </summary>
public sealed class ShaderHandlerRegistry : IService
{
    private readonly IShaderHandler[] _handlers =
    [
        new CharacterShaderHandler(),
        new CharacterLegacyShaderHandler(),
        new SkinShaderHandler(),
        new HairShaderHandler(),
        new FallbackShaderHandler(),
    ];

    public IShaderHandler For(string shpkName)
        => _handlers.First(h => h.Matches(shpkName));

    public IShaderHandler For(MtrlFile material)
        => For(material.ShaderPackage.Name);
}
