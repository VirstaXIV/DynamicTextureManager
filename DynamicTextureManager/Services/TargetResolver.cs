using System;
using System.Collections.Generic;
using System.Linq;
using DynamicTextureManager.Interop;
using OtterGui.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;

namespace DynamicTextureManager.Services;

/// <summary> One material found on a character, with everything needed to select it as a source. </summary>
public sealed record ResolvedMaterial(
    string GamePath,
    string ActualPath,
    string Label,
    string ModDirectory,
    string ModName,
    string MdlGamePath,
    string MdlActualPath)
{
    public bool IsModded
        => ModDirectory.Length > 0;
}

/// <summary> A group of materials belonging to one model (equipment piece, body part, ...). </summary>
public sealed record ResolvedModelGroup(string Label, IReadOnlyList<ResolvedMaterial> Materials);

/// <summary>
/// Resolves editing targets to concrete material lists. The primary path reads Penumbra's
/// resource trees, which already contain IMC-variant- and mod-resolved actual paths.
/// </summary>
public sealed class TargetResolver(PenumbraService penumbra) : IService
{
    /// <summary> All model groups with materials on the local player (object index 0). </summary>
    public IReadOnlyList<ResolvedModelGroup> ResolvePlayer()
    {
        if (!penumbra.Available)
            return [];

        var tree = penumbra.GetGameObjectResourceTrees(true, 0).FirstOrDefault();
        if (tree == null)
            return [];

        // Fetched once per resolve so identifying each material's owning mod stays cheap.
        var modRoot = penumbra.GetModDirectory();
        var modList = penumbra.GetModList();

        var groups = new List<ResolvedModelGroup>();
        foreach (var node in tree.Nodes)
            Collect(node, node.Name ?? "Unknown", groups, modRoot, modList);

        return groups;
    }

    private static void Collect(ResourceNodeDto node, string groupLabel, List<ResolvedModelGroup> groups, string modRoot,
        Dictionary<string, string> modList)
    {
        if (node.Type is ResourceType.Mdl)
        {
            var materials = new List<ResolvedMaterial>();
            CollectMaterials(node, materials, modRoot, modList, node.GamePath ?? string.Empty, node.ActualPath);
            if (materials.Count > 0)
                groups.Add(new ResolvedModelGroup(node.Name ?? groupLabel, materials));
            return;
        }

        foreach (var child in node.Children)
            Collect(child, node.Name ?? groupLabel, groups, modRoot, modList);
    }

    private static void CollectMaterials(ResourceNodeDto node, List<ResolvedMaterial> materials, string modRoot,
        Dictionary<string, string> modList, string mdlGamePath, string mdlActualPath)
    {
        foreach (var child in node.Children)
        {
            if (child.Type is ResourceType.Mtrl && child.GamePath != null)
            {
                if (!materials.Any(m => string.Equals(m.GamePath, child.GamePath, StringComparison.OrdinalIgnoreCase)))
                {
                    var mod = PenumbraService.IdentifyModOfFile(child.ActualPath, modRoot, modList);
                    materials.Add(new ResolvedMaterial(child.GamePath, child.ActualPath, child.Name ?? child.GamePath,
                        mod?.ModDirectory ?? string.Empty, mod?.ModName ?? string.Empty, mdlGamePath, mdlActualPath));
                }
            }

            CollectMaterials(child, materials, modRoot, modList, mdlGamePath, mdlActualPath);
        }
    }
}
