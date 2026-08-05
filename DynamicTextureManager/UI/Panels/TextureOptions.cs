using System;
using System.Collections.Generic;
using System.Linq;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.ModGeneration;
using DynamicTextureManager.ModGeneration.Shaders;
using Penumbra.GameData.Files;

namespace DynamicTextureManager.UI.Panels;

/// <summary> One selectable texture of a source material, classified by its shader handler. </summary>
public sealed record TextureOption(
    string GamePath,
    TextureSlot Slot,
    string MaterialLabel,
    bool DecalRecommended,
    string MaterialGamePath,
    MtrlFile Mtrl,
    MaterialKind Kind);

/// <summary> Shared material/texture enumeration and the material dropdown of the Decals and Textures tabs. </summary>
public static class TextureOptions
{
    /// <summary> All textures of the source materials, classified by shader handler. </summary>
    public static List<TextureOption> Collect(DTextureData data, SourceFileProvider sourceFiles, ShaderHandlerRegistry shaderHandlers)
    {
        var ret = new List<TextureOption>();
        foreach (var source in data.Source.Materials)
        {
            var mtrl = sourceFiles.GetMaterial(source, null);
            if (mtrl == null)
                continue;

            var handler = shaderHandlers.For(mtrl);
            var kind    = handler.Kind(mtrl);
            foreach (var info in handler.ClassifyTextures(mtrl))
            {
                if (!ret.Any(o => string.Equals(o.GamePath, info.GamePath, StringComparison.OrdinalIgnoreCase)))
                    ret.Add(new TextureOption(info.GamePath, info.Slot, source.Label, info.SupportsDecals, source.GamePath, mtrl, kind));
            }
        }

        return ret;
    }

}
