using System;
using System.Collections.Generic;
using System.IO;
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
    ShaderHandlerRegistry shaderHandlers,
    ModelUvReader uvReader,
    OverlayModManager overlayMods,
    SkinColorReader skinColorReader,
    Configuration config,
    CompositePreviewCache previewCache)
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
            ImUtf8.Text("No materials selected yet. Load your worn gear or skin below and add the materials to edit."u8);
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
            LoadPlayer(skin: false);
        ImUtf8.HoverTooltip("Read the worn equipment models and materials of your character through Penumbra."u8);

        ImGui.SameLine();
        if (ImUtf8.Button("Load Skin"u8))
            LoadPlayer(skin: true);
        ImUtf8.HoverTooltip("Read your character's skin materials (body, legs, face).\nDecals on skin bake into the skin texture like tattoos and conform to the body."u8);

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

    private void LoadPlayer(bool skin)
    {
        try
        {
            var groups = resolver.ResolvePlayer();
            _groups = skin ? FilterSkinGroups(groups) : FilterGearGroups(groups);
            _error = _groups.Count == 0
                ? skin
                    ? "No skin materials found — is your character loaded?"
                    : "No materials found — is your character loaded?"
                : string.Empty;

            // Load Skin implies the user wants a preview of THEIR body — match the preview
            // tone to the real character automatically, unless they already picked one
            // deliberately (manual ColorEdit or the "Use my character's skin color" button).
            if (skin && _groups.Count > 0 && !config.PreviewSkinToneUserSet
             && skinColorReader.TryGetLocalPlayerSkin(out var liveTone))
            {
                config.PreviewSkinTone = new SixLabors.ImageSharp.PixelFormats.Rgba32(liveTone.X, liveTone.Y, liveTone.Z).PackedValue;
                config.Save();
            }
        }
        catch (Exception ex)
        {
            _error  = $"Could not read resource trees: {ex.Message}";
            _groups = [];
            DynamicTextureManager.Log.Error($"Could not resolve player resources:\n{ex}");
        }
    }

    /// <summary> Equipment, accessory and weapon models — the character's own body parts live behind Load Skin. </summary>
    private static IReadOnlyList<ResolvedModelGroup> FilterGearGroups(IReadOnlyList<ResolvedModelGroup> groups)
        => groups.Where(g => !IsHumanModel(g)).ToList();

    /// <summary>
    /// The character's skin materials. Body skin has no model node of its own — the game has
    /// no nude body model, and every worn gear model embeds only the skin patches it exposes —
    /// so body materials are collected from anywhere in the tree and paired with the
    /// SmallClothes body models the mesh reader loads for them. Face (and other chara/human
    /// models) keep their own model, narrowed to skin materials — the face model also carries
    /// iris/occlusion/etc. materials decals cannot target.
    /// </summary>
    private IReadOnlyList<ResolvedModelGroup> FilterSkinGroups(IReadOnlyList<ResolvedModelGroup> groups)
    {
        var ret  = new List<ResolvedModelGroup>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var body = new List<(ResolvedMaterial Material, string Diffuse)>();
        foreach (var group in groups)
        foreach (var material in group.Materials)
        {
            if (!ModelUvReader.IsBodySkinMaterial(material.GamePath) || !seen.Add(material.GamePath))
                continue;

            var (isSkin, diffuse) = SkinInfo(material);
            if (isSkin)
                body.Add((material, diffuse));
        }

        if (body.Count > 0)
        {
            // The body race comes from the models actually WORN, not the material path — body
            // mod families deliberately use foreign race codes in their material paths (e.g.
            // bibo's c0101-pathed material on a c0201 female body).
            var race     = EquipmentBodyRace(groups) ?? ModelUvReader.BodyMaterialRace(body[0].Material.GamePath);
            var topModel = ModelUvReader.BodyModelSetForRace(race)[0];

            // The resource tree also surfaces skin materials the body does NOT render with
            // (e.g. the vanilla _a material while a body mod is active) — those only show on
            // stray gear-embedded patches, so decals on them are effectively invisible. Keep
            // the materials the resolved body models actually reference.
            var active = uvReader.ResolvedBodyMaterialNames(race);
            var usable = body.Where(e => active.Contains(Path.GetFileName(e.Material.GamePath))).ToList();
            if (usable.Count == 0)
                usable = body;

            // Several body materials painting the SAME diffuse texture are one canvas (body
            // mods split torso/legs into materials sharing one full-body texture, and decals
            // continue across that seam) — list the shared canvas once.
            var byDiffuse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped   = usable.Where(e => e.Diffuse.Length == 0 || byDiffuse.Add(e.Diffuse)).ToList();

            ret.Add(new ResolvedModelGroup("Body",
                deduped.Select(e => e.Material with { MdlGamePath = topModel, MdlActualPath = string.Empty }).ToList()));

            // Overlay parts sharing the same SmallClothes models as the body (nails, claws,
            // accents) — materials with their OWN diffuse texture that a body tattoo can
            // plausibly continue onto. Colorset-only pieces (piercings) and hair-shader pieces
            // (pubic hair) are excluded there — decals cannot paint them via the diffuse-bake
            // mechanism this offers. See ModelUvReader.GetBodyOverlayMaterials.
            var bodySource = new SourcePath { GamePath = body[0].Material.GamePath, ActualPath = body[0].Material.ActualPath };
            var overlayMaterials = uvReader.GetBodyOverlayMaterials(bodySource)
                .Select(o => ResolveOverlayMaterial(o, topModel))
                .ToList();
            if (overlayMaterials.Count > 0)
                ret.Add(new ResolvedModelGroup("Overlay Parts", overlayMaterials));
        }

        foreach (var group in groups.Where(IsHumanModel))
        {
            var skinMaterials = group.Materials.Where(m => seen.Add(m.GamePath) && IsSkinMaterial(m)).ToList();
            if (skinMaterials.Count > 0)
                ret.Add(new ResolvedModelGroup(group.Label, skinMaterials));
        }

        return ret;
    }

    /// <summary> Turn a discovered overlay-part material into a pickable entry, resolving its actual file and owning mod. </summary>
    private ResolvedMaterial ResolveOverlayMaterial(ModelUvReader.BodyOverlayMaterial overlay, string topModel)
    {
        var actual = penumbra.ResolvePlayerPath(overlay.GamePath);
        var mod    = Path.IsPathRooted(actual) ? penumbra.IdentifyModOfFile(actual) : null;
        return new ResolvedMaterial(overlay.GamePath, actual, OverlayLabel(overlay.Name), mod?.ModDirectory ?? string.Empty,
            mod?.ModName ?? string.Empty, topModel, string.Empty) { IsOverlayPart = true };
    }

    private static readonly System.Text.RegularExpressions.Regex OverlayMaterialNamePattern =
        new(@"^mt_c\d{4}b\d{4}_(.+)\.mtrl$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary> A friendlier label than the raw material file name, e.g. "mt_c0201b0001_trenails.mtrl" -> "Trenails". </summary>
    private static string OverlayLabel(string materialFileName)
    {
        var match = OverlayMaterialNamePattern.Match(materialFileName);
        var stem  = match.Success ? match.Groups[1].Value : materialFileName;
        return stem.Length == 0 ? materialFileName : char.ToUpperInvariant(stem[0]) + stem[1..];
    }

    private static bool IsHumanModel(ResolvedModelGroup group)
        => group.Materials.Count > 0
         && group.Materials[0].MdlGamePath.StartsWith("chara/human/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The body race code (cXXXX) the character's worn body-covering equipment models resolve
    /// with — the most common one wins, since a race-specific piece (tail/ear cutouts) can
    /// deviate from the shared body race.
    /// </summary>
    private static string? EquipmentBodyRace(IReadOnlyList<ResolvedModelGroup> groups)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        foreach (var material in group.Materials)
        {
            var match = BodySlotModelPattern.Match(material.MdlGamePath);
            if (match.Success)
                counts[match.Groups[1].Value] = counts.GetValueOrDefault(match.Groups[1].Value) + 1;
            break; // one model per group — its first material carries the model path
        }

        return counts.Count == 0 ? null : counts.MaxBy(kvp => kvp.Value).Key;
    }

    private static readonly System.Text.RegularExpressions.Regex BodySlotModelPattern =
        new(@"^chara/equipment/e\d{4}/model/(c\d{4})e\d{4}_(?:top|dwn|glv|sho)\.mdl$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private bool IsSkinMaterial(ResolvedMaterial material)
        => SkinInfo(material).IsSkin;

    /// <summary> Whether a material is skin, and which diffuse texture it paints (the tattoo canvas). </summary>
    private (bool IsSkin, string Diffuse) SkinInfo(ResolvedMaterial material)
    {
        var mtrl = sourceFiles.GetMaterial(new SourcePath { GamePath = material.GamePath, ActualPath = material.ActualPath }, null);
        if (mtrl == null)
            return (false, string.Empty);

        var handler = shaderHandlers.For(mtrl);
        if (handler.Kind(mtrl) is not MaterialKind.Skin)
            return (false, string.Empty);

        var diffuse = handler.ClassifyTextures(mtrl).FirstOrDefault(t => t.Slot is TextureSlot.Diffuse).GamePath ?? string.Empty;
        return (true, diffuse);
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
            Overlay       = material.IsOverlayPart,
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
        // Adding/removing a source material never publishes DTextureChanged (that event is
        // only for whole-dTexture create/delete/rename) and this tab never used to have the
        // preview cache injected at all — so a removed-then-re-added material's cached preview
        // Entry had no trigger to ever go stale, and Get() kept serving its old Pristine/
        // Composited buffers forever, regardless of what the data actually said (2026-07,
        // reported as "the preview still shows the previous decal" after remove+re-add).
        previewCache.Invalidate(dTexture.Identifier);
        // Source changes affect the built mod too — removing a material must rebuild (or
        // clean out) the generated files, otherwise old baked decals keep applying.
        overlayMods.QueueAutoApply(dTexture);
    }
}
