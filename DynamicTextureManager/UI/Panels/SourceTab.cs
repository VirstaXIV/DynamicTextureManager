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
    HairColorReader hairColorReader,
    Configuration config,
    CompositePreviewCache previewCache,
    DecalsTab decalsTab)
    : IService
{
    private const uint WarningColor = 0xFF00A0FFu;

    private IReadOnlyList<ResolvedModelGroup> _groups      = [];
    private string                            _error       = string.Empty;
    private Guid                              _groupsOwner = Guid.Empty;

    public void Draw(DTexture dTexture)
    {
        // The loaded picker candidates belong to the dTexture they were loaded for —
        // switching to another one starts fresh, otherwise the previous mod's source list
        // lingers and invites adding the same pieces again (only flagged as a conflict).
        if (_groupsOwner != dTexture.Identifier)
        {
            _groupsOwner = dTexture.Identifier;
            _groups      = [];
            _error       = string.Empty;
        }

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
            ImUtf8.Text("Nothing selected yet. Load your worn gear, skin or hair below and add the pieces to edit."u8);
            return;
        }

        // Sources are MODEL units: one row per piece, its materials (hidden companions
        // included) travel with it. Entries without a recorded model (older saves) stand alone.
        var units = source.Materials
            .GroupBy(m => m.MdlGamePath.Length > 0 ? m.MdlGamePath : m.GamePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ImUtf8.TextWrapped($"Selected Sources ({units.Count}) — edits always rebuild from the captured source files, so changes to the base mod carry over on the next build.");

        string? remove = null;
        using (var table = ImUtf8.Table("##sourceUnits"u8, 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            if (!table)
                return;

            ImUtf8.TableSetupColumn("Source"u8);
            ImUtf8.TableSetupColumn("Based On"u8);
            ImUtf8.TableSetupColumn("Materials"u8);
            ImUtf8.TableSetupColumn(""u8, ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();

            foreach (var (unit, idx) in units.WithIndex())
            {
                using var id      = ImUtf8.PushId(idx);
                var       primary = unit.FirstOrDefault(m => !m.Overlay) ?? unit.First();

                ImGui.TableNextColumn();
                var label = $"{UnitLabel(primary.MdlGamePath, primary.GamePath)}: {primary.Label}";
                if (primary.Overlay)
                {
                    ImUtf8.Text(label);
                }
                else
                {
                    if (ImUtf8.Selectable(label))
                        decalsTab.SelectMaterial(primary.GamePath);
                    if (ImGui.IsItemHovered())
                        ImUtf8.HoverTooltip("Click to edit this piece — its textures and model show in the preview column."u8);
                }

                foreach (var material in unit)
                    DrawConflictMarker(material.GamePath, conflicts);

                ImGui.TableNextColumn();
                DrawModCell(primary.ModDirectory, primary.ModName, primary.ActualPath);

                ImGui.TableNextColumn();
                ImUtf8.Text(unit.Count() == 1 ? primary.GamePath : $"{unit.Count()} materials");
                if (ImGui.IsItemHovered())
                    ImUtf8.HoverTooltip(string.Join("\n", unit.Select(m => $"{m.Label}: {m.GamePath}")));

                ImGui.TableNextColumn();
                if (ImUtf8.SmallButton("Remove"u8))
                    remove = unit.Key;
                if (ImGui.IsItemHovered())
                    ImUtf8.HoverTooltip("Remove this piece and everything belonging to it. Its colorset edits and decals are removed too."u8);
            }
        }

        if (remove != null)
            RemoveUnit(dTexture, remove);
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
            LoadPlayer(PickerMode.Gear);
        ImUtf8.HoverTooltip("Read the worn equipment models and materials of your character through Penumbra."u8);

        ImGui.SameLine();
        if (ImUtf8.Button("Load Skin"u8))
            LoadPlayer(PickerMode.Skin);
        ImUtf8.HoverTooltip("Read your character's skin materials (body, legs, face).\nDecals on skin bake into the skin texture like tattoos and conform to the body."u8);

        ImGui.SameLine();
        if (ImUtf8.Button("Load Hair"u8))
            LoadPlayer(PickerMode.Hair);
        ImUtf8.HoverTooltip(
            "Read your character's current hairstyle materials.\nHair has no color texture — the game blends your hair and highlight colors by the normal map,\nso edits adjust where highlights appear and how the hair shines."u8);

        if (_error.Length > 0)
            ImUtf8.TextWrapped(_error);

        if (_groups.Count == 0)
            return;

        using var table = ImUtf8.Table("##pickerGroups"u8, 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg);
        if (!table)
            return;

        ImUtf8.TableSetupColumn(""u8, ImGuiTableColumnFlags.WidthFixed);
        ImUtf8.TableSetupColumn("Source"u8);
        ImUtf8.TableSetupColumn("From Mod"u8);
        ImUtf8.TableSetupColumn("Notes"u8);
        ImGui.TableHeadersRow();

        foreach (var (group, groupIdx) in _groups.WithIndex())
        {
            using var id = ImUtf8.PushId(groupIdx);
            DrawGroupRow(dTexture, group, conflicts);
        }
    }

    /// <summary>
    /// One pickable MODEL unit: the piece is added or removed as a whole — all its materials
    /// (and, for hair, the model's companion materials) come along. Editing still targets
    /// individual materials through the editing panel's material selector.
    /// </summary>
    private void DrawGroupRow(DTexture dTexture, ResolvedModelGroup group, Dictionary<string, List<string>> conflicts)
    {
        var sourceMaterials = dTexture.Data.Source.Materials;
        var addedCount = group.Materials.Count(m => sourceMaterials.Any(s
            => string.Equals(s.GamePath, m.GamePath, StringComparison.OrdinalIgnoreCase)));
        var added   = addedCount > 0;
        var primary = group.Materials.FirstOrDefault(m => !m.IsOverlayPart) ?? group.Materials[0];

        // With a generated overlay active, the resource tree reports a DTM mod as the file's
        // origin — that is never a clean base to capture, so adding is blocked until the
        // overlay is disabled and the piece reloaded. Already-added entries keep their
        // originally captured base and are unaffected.
        var fromOverlay = !added && group.Materials.Any(m
            => m.ModDirectory.StartsWith("DTM_", StringComparison.OrdinalIgnoreCase));

        ImGui.TableNextColumn();
        if (added)
        {
            if (ImUtf8.SmallButton("Remove"u8))
                RemoveUnit(dTexture, primary.MdlGamePath);
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("Remove this piece and everything belonging to it from the source. Its colorset edits and decals are removed too."u8);
        }
        else
        {
            using (ImRaii.Disabled(fromOverlay))
            {
                if (ImUtf8.SmallButton("Add"u8))
                    AddGroup(dTexture, group);
            }

            if (fromOverlay && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImUtf8.HoverTooltip(
                    "A file of this piece currently comes from a generated overlay mod — not a clean base to edit.\nDisable that overlay, then reload to capture the real source."u8);
        }

        ImGui.TableNextColumn();
        if (added)
        {
            if (ImUtf8.Selectable(group.Label))
                decalsTab.SelectMaterial(primary.GamePath);
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("Click to edit this piece — its textures and model show in the preview column."u8);

            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, 0xFF40C040u))
                ImUtf8.Text(addedCount < group.Materials.Count ? "(partially added)"u8 : "(added)"u8);
        }
        else
        {
            ImUtf8.Text(group.Label);
        }

        foreach (var material in group.Materials)
            DrawConflictMarker(material.GamePath, conflicts);
        if (ImGui.IsItemHovered())
            ImUtf8.HoverTooltip(string.Join("\n", group.Materials.Select(m => $"{m.Label}: {m.GamePath}")));

        ImGui.TableNextColumn();
        DrawModCell(primary.ModDirectory, primary.ModName, primary.ActualPath);

        ImGui.TableNextColumn();
        if (fromOverlay)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, WarningColor))
                ImUtf8.Text("overlay active"u8);
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("A generated overlay currently owns files of this piece — disable it and reload to add it."u8);
        }
        else if (group.Materials.Count > 1)
        {
            ImUtf8.Text($"{group.Materials.Count} materials");
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip(string.Join("\n", group.Materials.Select(m => m.Label)));
        }
    }

    private enum PickerMode
    {
        Gear,
        Skin,
        Hair,
    }

    private void LoadPlayer(PickerMode mode)
    {
        try
        {
            var groups = resolver.ResolvePlayer();
            _groups = mode switch
            {
                PickerMode.Skin => FilterSkinGroups(groups),
                PickerMode.Hair => FilterHairGroups(groups),
                _               => FilterGearGroups(groups),
            };
            _error = _groups.Count == 0
                ? mode switch
                {
                    PickerMode.Skin => "No skin materials found — is your character loaded?",
                    PickerMode.Hair => "No hair materials found — is your character loaded?",
                    _               => "No materials found — is your character loaded?",
                }
                : string.Empty;

            // Load Skin implies the user wants a preview of THEIR body — match the preview
            // tone to the real character automatically, unless they already picked one
            // deliberately (manual ColorEdit or the "Use my character's skin color" button).
            if (mode == PickerMode.Skin && _groups.Count > 0 && !config.PreviewSkinToneUserSet
             && skinColorReader.TryGetLocalPlayerSkin(out var liveTone))
            {
                config.PreviewSkinTone = new SixLabors.ImageSharp.PixelFormats.Rgba32(liveTone.X, liveTone.Y, liveTone.Z).PackedValue;
                config.Save();
            }

            // Same idea for hair: preview with the character's real hair and highlight colors
            // unless the user already picked their own.
            if (mode == PickerMode.Hair && _groups.Count > 0 && !config.PreviewHairColorsUserSet
             && hairColorReader.TryGetLocalPlayerHair(out var liveHair))
            {
                config.PreviewHairColor = new SixLabors.ImageSharp.PixelFormats.Rgba32(liveHair.Main.X, liveHair.Main.Y, liveHair.Main.Z).PackedValue;
                config.PreviewHairHighlight =
                    new SixLabors.ImageSharp.PixelFormats.Rgba32(liveHair.Highlight.X, liveHair.Highlight.Y, liveHair.Highlight.Z).PackedValue;
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

    /// <summary>
    /// Equipment, accessory and weapon models — the character's own body parts live behind
    /// Load Skin. One entry per worn piece, labeled and ordered by its slot.
    /// </summary>
    private static IReadOnlyList<ResolvedModelGroup> FilterGearGroups(IReadOnlyList<ResolvedModelGroup> groups)
        => groups.Where(g => !IsHumanModel(g) && g.Materials.Count > 0)
            .Select(g => (Slot: GearSlot(g.Materials[0].MdlGamePath), Group: g))
            .OrderBy(t => t.Slot.Order)
            .Select(t => new ResolvedModelGroup($"{t.Slot.Label}: {t.Group.Label}", t.Group.Materials))
            .ToList();

    /// <summary> The equipment/accessory slot of a model, from its file name suffix. </summary>
    private static (int Order, string Label) GearSlot(string mdlGamePath)
    {
        if (mdlGamePath.Contains("/weapon/", StringComparison.OrdinalIgnoreCase))
            return (10, "Weapon");

        var name   = Path.GetFileNameWithoutExtension(mdlGamePath);
        var suffix = name.Length >= 3 ? name[^3..].ToLowerInvariant() : string.Empty;
        return suffix switch
        {
            "met" => (0, "Head"),
            "top" => (1, "Body"),
            "glv" => (2, "Hands"),
            "dwn" => (3, "Legs"),
            "sho" => (4, "Feet"),
            "ear" => (5, "Earrings"),
            "nek" => (6, "Necklace"),
            "wrs" => (7, "Bracelets"),
            "rir" => (8, "Ring (Right)"),
            "ril" => (9, "Ring (Left)"),
            _     => (11, "Other"),
        };
    }

    /// <summary>
    /// The kind of unit a selected source material belongs to, for the model-based lists:
    /// sources are picked and removed as whole models, and this names them.
    /// </summary>
    private static string UnitLabel(string mdlGamePath, string materialGamePath)
    {
        if (ModelUvReader.IsBodySkinMaterial(materialGamePath))
            return "Body";
        if (HairModelPattern.IsMatch(mdlGamePath))
            return "Hair";
        if (mdlGamePath.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase))
            return "Face";
        if (mdlGamePath.Contains("/obj/tail/", StringComparison.OrdinalIgnoreCase))
            return "Tail";
        if (mdlGamePath.Contains("/human/", StringComparison.OrdinalIgnoreCase))
            return "Character";
        return GearSlot(mdlGamePath).Label;
    }

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

            // The body is ONE unit: its skin canvases plus the overlay parts sharing the same
            // SmallClothes models (nails, claws, accents — materials with their OWN diffuse a
            // body tattoo can continue onto; colorset-only piercings and hair-shader pieces
            // stay excluded, see ModelUvReader.GetBodyOverlayMaterials). Adding "Body" adds
            // everything related to the bare body.
            var bodySource = new SourcePath { GamePath = body[0].Material.GamePath, ActualPath = body[0].Material.ActualPath };
            var bodyUnit = deduped
                .Select(e => e.Material with { MdlGamePath = topModel, MdlActualPath = string.Empty })
                .Concat(uvReader.GetBodyOverlayMaterials(bodySource).Select(o => ResolveOverlayMaterial(o, topModel)))
                .ToList();
            ret.Add(new ResolvedModelGroup("Body", bodyUnit));
        }

        foreach (var group in groups.Where(IsHumanModel))
        {
            var skinMaterials = group.Materials.Where(m => seen.Add(m.GamePath) && IsSkinMaterial(m)).ToList();
            if (skinMaterials.Count > 0)
                ret.Add(new ResolvedModelGroup(group.Label, skinMaterials));
        }

        return ret;
    }

    /// <summary>
    /// The worn hairstyle's hair-shader materials. Hair models live under chara/human like the
    /// face, but only materials the hair handler actually supports are offered — face-variant
    /// hair.shpk materials (brows/lashes) reinterpret the highlight channel as the race-feature
    /// color and stay gated off.
    /// </summary>
    private IReadOnlyList<ResolvedModelGroup> FilterHairGroups(IReadOnlyList<ResolvedModelGroup> groups)
    {
        var ret = new List<ResolvedModelGroup>();
        foreach (var group in groups)
        {
            if (group.Materials.Count == 0 || !HairModelPattern.IsMatch(group.Materials[0].MdlGamePath))
                continue;

            var hairMaterials = group.Materials.Where(IsHairMaterial).ToList();
            if (hairMaterials.Count == 0)
                continue;

            // One hairstyle = ONE pickable entry, even when the style splits its strands
            // across several materials (an implementation detail of the model). Prefer the
            // material named after the model's own hair id (the scalp material); the sibling
            // materials are added automatically alongside it.
            var hairId  = HairIdPattern.Match(group.Materials[0].MdlGamePath).Groups[1].Value;
            var primary = hairMaterials.FirstOrDefault(m
                => hairId.Length > 0 && m.GamePath.Contains(hairId, StringComparison.OrdinalIgnoreCase)) ?? hairMaterials[0];
            ret.Add(new ResolvedModelGroup(group.Label, [primary]));
        }

        return ret;
    }

    private static readonly System.Text.RegularExpressions.Regex HairIdPattern =
        new(@"/hair/(h\d{4})/", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);


    private static readonly System.Text.RegularExpressions.Regex HairModelPattern =
        new(@"^chara/human/c\d{4}/obj/hair/h\d{4}/model/",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private bool IsHairMaterial(ResolvedMaterial material)
    {
        var mtrl = sourceFiles.GetMaterial(new SourcePath { GamePath = material.GamePath, ActualPath = material.ActualPath }, null);
        if (mtrl == null)
            return false;

        var kind = shaderHandlers.For(mtrl).Kind(mtrl);
        // Verification aid for the GetSubColor face-variant gate (the CRC pair is derived, not
        // observed): one line per hair-model material on each Load Hair, with its raw keys.
        DynamicTextureManager.Log.Debug(
            $"Hair candidate {material.GamePath}: shader {mtrl.ShaderPackage.Name}, kind {kind}, keys [{string.Join(", ", mtrl.ShaderPackage.ShaderKeys.Select(k => $"0x{k.Key:X8}=0x{k.Value:X8}"))}]");
        return kind is MaterialKind.Hair;
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

    /// <summary> Add a whole model unit: every material of the piece, plus hair companions. </summary>
    private void AddGroup(DTexture dTexture, ResolvedModelGroup group)
    {
        var source = dTexture.Data.Source;
        source.Type = SourceType.GamePath;
        if (source.DisplayName.Length == 0)
            source.DisplayName = "Worn Gear";

        string? select = null;
        foreach (var material in group.Materials)
        {
            if (source.Materials.Any(m => string.Equals(m.GamePath, material.GamePath, StringComparison.OrdinalIgnoreCase)))
                continue;

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

            // A hairstyle brings its model's sibling materials along as hidden companions.
            if (!material.IsOverlayPart && HairModelPattern.IsMatch(material.MdlGamePath))
                ModGeneration.HairSources.AddCompanions(dTexture.Data, source.Materials[^1], uvReader, sourceFiles, shaderHandlers, penumbra);

            select ??= material.IsOverlayPart ? null : material.GamePath;
        }

        // The freshly added piece is almost always what the user wants to edit next.
        if (select != null)
            decalsTab.SelectMaterial(select);
        Save(dTexture);
    }

    /// <summary>
    /// Remove a whole model unit: every source material of the model (hidden companions
    /// included — they share the model path) with its colorset edits, animated-hair configs
    /// and orphaned texture stacks.
    /// </summary>
    private void RemoveUnit(DTexture dTexture, string unitKey)
    {
        var source  = dTexture.Data.Source;
        var removed = source.Materials
            .Where(m => string.Equals(m.MdlGamePath.Length > 0 ? m.MdlGamePath : m.GamePath, unitKey, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.GamePath)
            .ToList();
        if (removed.Count == 0)
            return;

        foreach (var path in removed)
        {
            source.Materials.RemoveAll(m => string.Equals(m.GamePath, path, StringComparison.OrdinalIgnoreCase));
            dTexture.Data.Materials.Remove(path);
            dTexture.Data.AnimatedHair.Remove(path);
        }

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
