using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Interop;
using DynamicTextureManager.ModGeneration;
using DynamicTextureManager.ModGeneration.Shaders;
using DynamicTextureManager.Services;
using OtterGui.Extensions;
using OtterGui.Raii;
using OtterGui.Services;
using OtterGui.Text;

namespace DynamicTextureManager.UI.Panels;

/// <summary>
/// Tab for picking what a dTexture overlays. Materials are added and removed one at a time;
/// the picker marks materials that are already part of the source (keeping the mod they are
/// based on visible) and flags clashes with other dTextures targeting the same file.
/// </summary>
public sealed class SourceTab(
    TargetResolver resolver,
    PenumbraService penumbra,
    SaveService saveService,
    DTextureStorage storage,
    SourceFileProvider sourceFiles,
    ShaderHandlerRegistry shaderHandlers)
    : IService
{
    private const uint WarningColor = 0xFF00A0FFu;

    private IReadOnlyList<ResolvedModelGroup> _groups = [];
    private string                            _error  = string.Empty;

    public void Draw(DTexture dTexture)
    {
        var conflicts = BuildConflictMap(dTexture);
        DrawCurrentSource(dTexture, conflicts);
        ImGui.Separator();
        DrawPlayerPicker(dTexture, conflicts);
    }

    /// <summary> Game paths other dTextures also target — both generated mods would override the same file. </summary>
    private Dictionary<string, List<string>> BuildConflictMap(DTexture current)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in storage.Where(d => d.Identifier != current.Identifier))
        {
            foreach (var material in other.Data.Source.Materials)
            {
                if (!map.TryGetValue(material.GamePath, out var names))
                    map[material.GamePath] = names = [];
                names.Add(other.Name.Text);
            }
        }

        return map;
    }

    private void DrawCurrentSource(DTexture dTexture, Dictionary<string, List<string>> conflicts)
    {
        var source = dTexture.Data.Source;
        if (source.IsEmpty)
        {
            ImUtf8.Text("No materials selected yet. Load your worn gear below and add the materials to edit."u8);
            return;
        }

        ImUtf8.TextWrapped($"Selected Materials ({source.Materials.Count}) — edits always rebuild from these source files, so changes to the base mod carry over on the next build.");

        string? remove = null;
        using (var table = ImUtf8.Table("##sourceMaterials"u8, 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            if (!table)
                return;

            ImUtf8.TableSetupColumn("Material"u8);
            ImUtf8.TableSetupColumn("Based On"u8);
            ImUtf8.TableSetupColumn("Game Path"u8);
            ImUtf8.TableSetupColumn(""u8, ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();

            foreach (var (material, idx) in source.Materials.WithIndex())
            {
                using var id = ImUtf8.PushId(idx);
                ImGui.TableNextColumn();
                ImUtf8.Text(material.Label);
                DrawConflictMarker(material.GamePath, conflicts);
                ImGui.TableNextColumn();
                DrawModCell(material.ModDirectory, material.ModName, material.ActualPath);
                ImGui.TableNextColumn();
                ImUtf8.Text(material.GamePath);
                if (ImGui.IsItemHovered())
                    ImUtf8.HoverTooltip($"Game Path: {material.GamePath}\nActual File: {material.ActualPath}");
                ImGui.TableNextColumn();
                if (ImUtf8.SmallButton("Remove"u8))
                    remove = material.GamePath;
                if (ImGui.IsItemHovered())
                    ImUtf8.HoverTooltip("Remove this material from the source. Its colorset edits and decals are removed too."u8);
            }
        }

        if (remove != null)
            RemoveMaterial(dTexture, remove);
    }

    /// <summary> An inline warning when another dTexture also targets this game path. </summary>
    private static void DrawConflictMarker(string gamePath, Dictionary<string, List<string>> conflicts)
    {
        if (!conflicts.TryGetValue(gamePath, out var names))
            return;

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, WarningColor))
            ImUtf8.Text("[conflict]"u8);
        if (ImGui.IsItemHovered())
            ImUtf8.HoverTooltip(
                $"Also targeted by: {string.Join(", ", names)}.\nBoth generated mods would override this material — only the one with the higher Penumbra priority takes effect.");
    }

    /// <summary> Shows the owning mod of a file as a clickable link opening it in Penumbra, or "Vanilla". </summary>
    private void DrawModCell(string modDirectory, string modName, string actualPath)
    {
        if (modDirectory.Length == 0)
        {
            ImUtf8.Text("Vanilla"u8);
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("Unmodified game file."u8);
            return;
        }

        if (ImUtf8.SmallButton($"{modName}##openMod"))
            penumbra.OpenModInPenumbra(modDirectory);
        if (ImGui.IsItemHovered())
            ImUtf8.HoverTooltip($"Provided by mod \"{modName}\" ({modDirectory}).\nFile: {actualPath}\nClick to open this mod in Penumbra.");
    }

    private void DrawPlayerPicker(DTexture dTexture, Dictionary<string, List<string>> conflicts)
    {
        if (!penumbra.Available)
        {
            ImUtf8.Text("Penumbra is not available."u8);
            return;
        }

        if (ImUtf8.Button("Load Worn Gear"u8))
            LoadPlayer();
        ImUtf8.HoverTooltip("Read the current models and materials of your character through Penumbra."u8);

        if (_error.Length > 0)
            ImUtf8.TextWrapped(_error);

        foreach (var (group, groupIdx) in _groups.WithIndex())
        {
            using var id = ImUtf8.PushId(groupIdx);
            if (!ImUtf8.CollapsingHeader(group.Label))
                continue;

            using var table = ImUtf8.Table("##pickerMaterials"u8, 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg);
            if (!table)
                continue;

            ImUtf8.TableSetupColumn(""u8, ImGuiTableColumnFlags.WidthFixed);
            ImUtf8.TableSetupColumn("Material"u8);
            ImUtf8.TableSetupColumn("From Mod"u8);
            ImUtf8.TableSetupColumn("Notes"u8);

            foreach (var (material, idx) in group.Materials.WithIndex())
            {
                using var rowId = ImUtf8.PushId(idx);
                DrawPickerRow(dTexture, material, conflicts);
            }
        }
    }

    private void DrawPickerRow(DTexture dTexture, ResolvedMaterial material, Dictionary<string, List<string>> conflicts)
    {
        var added = dTexture.Data.Source.Materials.FirstOrDefault(m
            => string.Equals(m.GamePath, material.GamePath, StringComparison.OrdinalIgnoreCase));
        // With a generated overlay active, the resource tree reports a DTM mod as the file's
        // origin — that is never a clean base to capture, so adding is blocked until the
        // overlay is disabled and the gear reloaded. Already-added entries keep their
        // originally captured base and are unaffected.
        var fromOverlay = added == null && material.ModDirectory.StartsWith("DTM_", StringComparison.OrdinalIgnoreCase);

        ImGui.TableNextColumn();
        if (added != null)
        {
            if (ImUtf8.SmallButton("Remove"u8))
                RemoveMaterial(dTexture, material.GamePath);
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("Remove this material from the source. Its colorset edits and decals are removed too."u8);
        }
        else
        {
            using (ImRaii.Disabled(fromOverlay))
            {
                if (ImUtf8.SmallButton("Add"u8))
                    AddMaterial(dTexture, material);
            }

            if (fromOverlay && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImUtf8.HoverTooltip(
                    $"This file currently comes from the generated overlay mod \"{material.ModName}\" — not a clean base to edit.\nDisable that overlay, then Load Worn Gear again to capture the real source.");
        }

        ImGui.TableNextColumn();
        ImUtf8.Text(material.Label);
        if (added != null)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, 0xFF40C040u))
                ImUtf8.Text("(added)"u8);
        }

        DrawConflictMarker(material.GamePath, conflicts);
        if (ImGui.IsItemHovered())
            ImUtf8.HoverTooltip($"Game Path: {material.GamePath}\nActual File: {material.ActualPath}");

        // For added materials show the base they were captured from — the current resolve may
        // point at our own overlay, but edits are built on the stored base regardless.
        ImGui.TableNextColumn();
        if (added != null)
            DrawModCell(added.ModDirectory, added.ModName, added.ActualPath);
        else
            DrawModCell(material.ModDirectory, material.ModName, material.ActualPath);

        ImGui.TableNextColumn();
        if (fromOverlay)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, WarningColor))
                ImUtf8.Text("overlay active"u8);
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("A generated overlay currently owns this file — disable it and reload to add this material."u8);
        }
    }

    private void LoadPlayer()
    {
        try
        {
            _groups = resolver.ResolvePlayer();
            _error  = _groups.Count == 0 ? "No materials found — is your character loaded?" : string.Empty;
        }
        catch (Exception ex)
        {
            _error  = $"Could not read resource trees: {ex.Message}";
            _groups = [];
            DynamicTextureManager.Log.Error($"Could not resolve player resources:\n{ex}");
        }
    }

    private void AddMaterial(DTexture dTexture, ResolvedMaterial material)
    {
        var source = dTexture.Data.Source;
        if (source.Materials.Any(m => string.Equals(m.GamePath, material.GamePath, StringComparison.OrdinalIgnoreCase)))
            return;

        source.Type = SourceType.GamePath;
        if (source.DisplayName.Length == 0)
            source.DisplayName = "Worn Gear";
        source.Materials.Add(new SourcePath
        {
            GamePath      = material.GamePath,
            ActualPath    = material.ActualPath,
            Label         = material.Label,
            ModDirectory  = material.ModDirectory,
            ModName       = material.ModName,
            MdlGamePath   = material.MdlGamePath,
            MdlActualPath = material.MdlActualPath,
        });
        Save(dTexture);
    }

    private void RemoveMaterial(DTexture dTexture, string gamePath)
    {
        var source = dTexture.Data.Source;
        if (source.Materials.RemoveAll(m => string.Equals(m.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)) == 0)
            return;

        dTexture.Data.Materials.Remove(gamePath);
        PruneOrphanedTextures(dTexture);
        Save(dTexture);
    }

    /// <summary>
    /// Drop layer stacks on textures no remaining source material exposes — they would
    /// otherwise keep being baked invisibly. Skipped entirely when any remaining material
    /// cannot be loaded, so a temporary resolve failure never deletes valid layers.
    /// </summary>
    private void PruneOrphanedTextures(DTexture dTexture)
    {
        if (dTexture.Data.Textures.Count == 0)
        {
            dTexture.Data.TextureSourcePaths.Clear();
            return;
        }

        var exposed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in dTexture.Data.Source.Materials)
        {
            var mtrl = sourceFiles.GetMaterial(material, null);
            if (mtrl == null)
                return;

            foreach (var info in shaderHandlers.For(mtrl).ClassifyTextures(mtrl))
                exposed.Add(info.GamePath);
        }

        foreach (var orphan in dTexture.Data.Textures.Keys.Where(k => !exposed.Contains(k)).ToList())
        {
            dTexture.Data.Textures.Remove(orphan);
            dTexture.Data.TextureSourcePaths.Remove(orphan);
        }
    }

    private void Save(DTexture dTexture)
    {
        dTexture.LastEdit = DateTimeOffset.UtcNow;
        saveService.QueueSave(dTexture);
    }
}
