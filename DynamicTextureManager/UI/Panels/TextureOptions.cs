using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.ModGeneration;
using DynamicTextureManager.ModGeneration.Shaders;
using OtterGui.Text;
using Penumbra.GameData.Files;

namespace DynamicTextureManager.UI.Panels;

/// <summary> One selectable texture of a source material, classified by its shader handler. </summary>
public sealed record TextureOption(
    string GamePath,
    TextureSlot Slot,
    string MaterialLabel,
    bool DecalRecommended,
    string MaterialGamePath,
    MtrlFile Mtrl);

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

            foreach (var info in shaderHandlers.For(mtrl).ClassifyTextures(mtrl))
            {
                if (!ret.Any(o => string.Equals(o.GamePath, info.GamePath, StringComparison.OrdinalIgnoreCase)))
                    ret.Add(new TextureOption(info.GamePath, info.Slot, source.Label, info.SupportsDecals, source.GamePath, mtrl));
            }
        }

        return ret;
    }

    /// <summary> The display label of one texture slot in the texture dropdowns. </summary>
    public static string SlotLabel(TextureOption option)
        => option.Slot is TextureSlot.Index ? "Colorset (ID map)" : option.Slot.ToString();

    /// <summary>
    /// Dropdown over the distinct materials of the options. Ensures the selection is valid
    /// (falling back to the first material) and returns true when it changed.
    /// </summary>
    public static bool DrawMaterialCombo(IReadOnlyList<TextureOption> options, ref string selectedMaterial)
    {
        var materials = new List<(string GamePath, string Label)>();
        foreach (var option in options)
            if (!materials.Any(m => string.Equals(m.GamePath, option.MaterialGamePath, StringComparison.OrdinalIgnoreCase)))
                materials.Add((option.MaterialGamePath, option.MaterialLabel));

        if (materials.Count == 0)
            return false;

        var changed = false;
        var currentPath = selectedMaterial;
        var current = materials.FirstOrDefault(m => string.Equals(m.GamePath, currentPath, StringComparison.OrdinalIgnoreCase));
        if (current.GamePath == null)
        {
            current          = materials[0];
            selectedMaterial = current.GamePath;
            changed          = true;
        }

        using var combo = ImUtf8.Combo("##material"u8, current.Label);
        if (combo)
            foreach (var material in materials)
            {
                if (ImUtf8.Selectable($"{material.Label}##{material.GamePath}",
                        string.Equals(material.GamePath, selectedMaterial, StringComparison.OrdinalIgnoreCase))
                 && !string.Equals(material.GamePath, selectedMaterial, StringComparison.OrdinalIgnoreCase))
                {
                    selectedMaterial = material.GamePath;
                    changed          = true;
                }

                if (ImGui.IsItemHovered())
                    ImUtf8.HoverTooltip(material.GamePath);
            }

        return changed;
    }
}
