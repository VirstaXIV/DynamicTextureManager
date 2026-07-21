using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
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
using Penumbra.GameData.Files;
using Penumbra.GameData.Files.MaterialStructs;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.UI.Panels;

/// <summary> Tab for stamping decals onto the textures of the selected source materials. </summary>
public sealed class TexturesTab(
    SourceFileProvider sourceFiles,
    ShaderHandlerRegistry shaderHandlers,
    DecalLibrary decals,
    TextureIO textureIO,
    ITextureProvider textureProvider,
    SaveService saveService,
    OverlayModManager overlayMods,
    EditPreviewer previewer,
    RowHighlighter highlighter,
    ModelUvReader uvReader,
    TargetResolver resolver,
    CharacterModelState modelState,
    TextureCompositor compositor,
    PenumbraService penumbra,
    Configuration config)
    : IService, IDisposable
{
    private const long  SlotPreviewDebounceMs = 400;

    /// <summary> Darkening applied to a shade-partner row: the benign blend target for a pair's unused half. </summary>
    private const float ShadeFactor = 0.6f;

    private bool           _slotPreviewDirty;
    private long           _slotPreviewMs;
    private TextureOption? _slotPreviewOption;

    private sealed record TextureOption(
        string GamePath,
        TextureSlot Slot,
        string MaterialLabel,
        bool DecalRecommended,
        string MaterialGamePath,
        MtrlFile Mtrl);

    private readonly FileDialogManager _fileDialog = new();

    private Guid                 _cacheOwner = Guid.Empty;
    private string               _sourceFingerprint = string.Empty;
    private List<TextureOption>? _options;
    private string               _selectedTexture = string.Empty;
    private bool                 _highlightHovered;

    private readonly DecalViewport _viewport = new(textureProvider);

    private string                _previewPath = string.Empty;
    private IDalamudTextureWrap?  _previewWrap;
    private string                _statsTexture = string.Empty;
    private readonly HashSet<int> _usedRowPairs   = [];
    private readonly Dictionary<int, int> _rowUsageCounts = [];

    public void Dispose()
    {
        _previewWrap?.Dispose();
        _viewport.Dispose();
    }

    public void Draw(DTexture dTexture)
    {
        _highlightHovered = false;
        DrawInner(dTexture);
        if (!_highlightHovered)
            highlighter.Clear();
    }

    private void DrawInner(DTexture dTexture)
    {
        _fileDialog.Draw();

        // Rebuild the texture list when the selection or its source materials change.
        var fingerprint = string.Join('\n', dTexture.Data.Source.Materials.Select(m => m.GamePath));
        if (_cacheOwner != dTexture.Identifier || _sourceFingerprint != fingerprint)
        {
            _cacheOwner        = dTexture.Identifier;
            _sourceFingerprint = fingerprint;
            _options           = null;
            _selectedTexture   = string.Empty;
            _mdlHealAttempted  = false;
            _previewDecoded    = null;
            _viewport.Close();
            previewer.Clear();
        }

        if (dTexture.Data.Source.IsEmpty)
        {
            ImUtf8.Text("Select a source first."u8);
            return;
        }

        _options ??= CollectOptions(dTexture);

        var showAll = config.ShowAllTextures;
        if (ImUtf8.Checkbox("Show All Textures (Advanced)"u8, ref showAll))
        {
            config.ShowAllTextures = showAll;
            config.Save();
        }

        ImGui.SameLine();
        ImUtf8.LabeledHelpMarker(""u8,
            "Decals are meant for color (diffuse/base) textures.\nThis also lists normal, mask, index and specular maps — stamping a color image onto those usually produces wrong shading, but can be useful if you know what you are doing."u8);

        var visible = showAll ? _options : _options.Where(o => o.DecalRecommended).ToList();
        if (visible.Count == 0)
        {
            if (_options.Count == 0)
                ImUtf8.Text("The source materials expose no textures."u8);
            else
                ImUtf8.Text("The source materials expose neither a color (diffuse/base) nor an ID texture — \"Show All Textures\" above lists the remaining maps."u8);

            return;
        }

        DrawTextureSelector(dTexture, visible);

        // Colorset decals only work on current gear materials; other setups (legacy gear,
        // skin, hair, vfx) need their own flows and are gated off until those exist.
        var selected = _options.Find(o => string.Equals(o.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase));
        if (selected is { Slot: TextureSlot.Index, DecalRecommended: false })
        {
            ImUtf8.Text(
                "Colorset decals require a current (Dawntrail) gear material — legacy gear, skin and other texture types are not supported yet."u8);
            return;
        }

        ImGui.Separator();
        DrawDecalLibrary(dTexture);

        if (_selectedTexture.Length == 0)
            return;

        ImGui.Separator();
        DrawLayers(dTexture);
        DrawStrayRows(dTexture);
        ImGui.Separator();
        DrawPreview(dTexture);
        UpdateSlotPreview(dTexture);
        _viewport.Draw(dTexture);
    }

    /// <summary>
    /// Edited colorset rows on this material that no decal slot owns still affect the gear
    /// invisibly from this tab — list them so leftovers from experiments are obvious.
    /// </summary>
    private void DrawStrayRows(DTexture dTexture)
    {
        var option = _options?.Find(o => string.Equals(o.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase));
        if (option == null || !dTexture.Data.Materials.TryGetValue(option.MaterialGamePath, out var edit) || edit.IsEmpty)
            return;

        var claimed = ClaimedRowsForMaterial(dTexture, option.MaterialGamePath, null);
        var strays  = edit.Rows.Keys.Where(r => !claimed.Contains(r)).OrderBy(r => r).ToList();
        if (strays.Count == 0)
            return;

        ImGui.Separator();
        ImUtf8.Text($"Other edited rows on this material: {string.Join(", ", strays.Select(RowName))}");
        ImUtf8.Text("These affect the gear too — leftovers from removed decals or older experiments."u8);
        if (ImUtf8.SmallButton("Clear These Rows"u8) && ImGui.GetIO().KeyCtrl)
        {
            foreach (var row in strays)
                edit.Rows.Remove(row);
            if (edit.IsEmpty)
                dTexture.Data.Materials.Remove(option.MaterialGamePath);
            Save(dTexture);
        }

        if (ImGui.IsItemHovered())
            ImUtf8.HoverTooltip("Hold Control and click to remove all listed row edits (the source values return)."u8);
    }

    private static string RowName(int row)
        => $"{row / 2 + 1}{(row % 2 == 0 ? 'A' : 'B')}";

    /// <summary> Debounced on-model preview of slot color/dye changes through a temporary mod. </summary>
    private void UpdateSlotPreview(DTexture dTexture)
    {
        if (!_slotPreviewDirty || !config.LivePreview || _slotPreviewOption == null)
            return;
        if (Environment.TickCount64 - _slotPreviewMs < SlotPreviewDebounceMs)
            return;

        _slotPreviewDirty = false;
        if (!dTexture.Data.Materials.TryGetValue(_slotPreviewOption.MaterialGamePath, out var edit) || edit.IsEmpty)
            return;

        var clone = MaterialEditApplier.CloneForEdit(_slotPreviewOption.Mtrl);
        if (MaterialEditApplier.Apply(clone, edit) > 0)
            previewer.Preview(_slotPreviewOption.MaterialGamePath, clone.Write());
    }

    /// <summary> All textures of the source materials, classified by shader handler. </summary>
    private List<TextureOption> CollectOptions(DTexture dTexture)
    {
        var ret = new List<TextureOption>();
        foreach (var source in dTexture.Data.Source.Materials)
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

    private void DrawTextureSelector(DTexture dTexture, List<TextureOption> visible)
    {
        var current = visible.Find(o => string.Equals(o.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase));
        var preview = current == null ? "Select Texture..." : $"{current.MaterialLabel} — {current.Slot}";
        using var combo = ImUtf8.Combo("##texture"u8, preview);
        if (!combo)
            return;

        foreach (var option in visible)
        {
            var layerCount = dTexture.Data.Textures.GetValueOrDefault(option.GamePath)?.Count ?? 0;
            var slotLabel  = option.Slot is TextureSlot.Index ? "Colorset Decal (ID map)" : option.Slot.ToString();
            var caution    = option.DecalRecommended ? string.Empty : "  (not recommended)";
            var label      = $"{option.MaterialLabel} — {slotLabel}{caution}{(layerCount > 0 ? $" ({layerCount} layer(s))" : string.Empty)}";
            if (ImUtf8.Selectable($"{label}##{option.GamePath}",
                    string.Equals(option.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase)))
                _selectedTexture = option.GamePath;
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip(option.DecalRecommended
                    ? option.GamePath
                    : $"{option.GamePath}\nThis is a {option.Slot} map, not a color texture — stamping a color decal here usually produces wrong shading.");
        }
    }

    private void DrawDecalLibrary(DTexture dTexture)
    {
        if (ImUtf8.Button("Import Decal..."u8))
            _fileDialog.OpenFileDialog("Import Decal", "Images{.png,.jpg,.jpeg,.dds,.bmp,.tga}", (success, path) =>
            {
                if (success)
                    decals.Import(path);
            });
        ImUtf8.HoverTooltip("Import an image into the decal library. It is converted to PNG and can be stamped onto textures."u8);

        if (decals.Decals.Count == 0)
        {
            ImUtf8.Text("No decals imported yet."u8);
            return;
        }

        var iconSize = new Vector2(48 * ImUtf8.GlobalScale);
        foreach (var (decal, idx) in decals.Decals.WithIndex())
        {
            using var id = ImUtf8.PushId(idx);
            if (idx > 0)
                ImGui.SameLine();

            using var group = ImUtf8.Group();
            var wrap = textureProvider.GetFromFile(decals.FilePath(decal.Id)).GetWrapOrDefault();
            if (wrap != null)
                ImGui.Image(wrap.Handle, iconSize);
            else
                ImGui.Dummy(iconSize);

            if (ImUtf8.SmallButton("Add"u8) && _selectedTexture.Length > 0)
                AddLayer(dTexture, decal.Id);
            ImGui.SameLine();
            if (ImUtf8.SmallButton("X"u8) && ImGui.GetIO().KeyCtrl)
                decals.Delete(decal.Id);

            group.Dispose();
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip(
                    $"{decal.Name}\nAdd: stamp this decal onto the selected texture.\nX: hold Control and click to remove from the library.");
        }
    }

    private void AddLayer(DTexture dTexture, Guid decalId)
    {
        // Index maps get colorset decals: the decal is quantized and each of its colors
        // remaps texels to an automatically claimed colorset row.
        var option = _options?.Find(o => string.Equals(o.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase));
        if (option is { Slot: TextureSlot.Index, DecalRecommended: false })
            return; // Colorset decals are gated to supported (Dawntrail gear) materials.

        if (!dTexture.Data.Textures.TryGetValue(_selectedTexture, out var layers))
        {
            layers                                    = [];
            dTexture.Data.Textures[_selectedTexture] = layers;
        }

        CaptureTextureSource(dTexture, _selectedTexture);

        var layer = new DecalLayer
        {
            DecalId   = decalId,
            IdRemap   = option?.Slot is TextureSlot.Index,
            MaxColors = config.DefaultDecalMaxColors,
        };
        layers.Add(layer);
        if (layer.IdRemap && option?.Mtrl.Table is ColorTable table)
            ReallocateDecal(dTexture, option, table, layer);
        Save(dTexture);
    }

    /// <summary>
    /// Record the pristine source file of a texture the first time it gets a layer, so
    /// rebuilds always start from the original instead of our own already-baked output.
    /// </summary>
    private void CaptureTextureSource(DTexture dTexture, string gamePath)
        => overlayMods.GetOrCaptureTextureSource(dTexture, gamePath);

    private void DrawLayers(DTexture dTexture)
    {
        if (!dTexture.Data.Textures.TryGetValue(_selectedTexture, out var layers) || layers.Count == 0)
        {
            ImUtf8.Text("No layers on this texture yet — add a decal from the library above."u8);
            return;
        }

        var remove = -1;
        var swap   = (-1, -1);
        foreach (var (layer, idx) in layers.WithIndex())
        {
            using var id = ImUtf8.PushId(idx);
            if (layer is not DecalLayer decal)
                continue;

            var name = decals.Get(decal.DecalId)?.Name ?? "(missing decal)";

            var enabled = decal.Enabled;
            if (ImUtf8.Checkbox("##enabled"u8, ref enabled))
            {
                decal.Enabled = enabled;
                // Re-enabling an auto-disabled colorset decal retries the row allocation.
                if (enabled && decal.IdRemap && decal.PaletteRows.Count == 0)
                    decal.RowError = null;
                Save(dTexture);
            }

            ImGui.SameLine();
            var modeTag = decal.Surface
                ? decal is { AnchorX: 0f, AnchorY: 0f, AnchorZ: 0f } ? "  [3D — not placed!]" : "  [3D]"
                : string.Empty;
            var errorTag = decal.RowError != null ? "  [auto-disabled]" : string.Empty;
            if (!ImUtf8.CollapsingHeader($"{idx + 1}: {name}{modeTag}{errorTag}###layer{idx}"))
                continue;

            using var indent = ImRaii.PushIndent();

            var changed = false;
            if (decal.IdRemap)
                changed |= DrawIdRemapSettings(dTexture, decal);

            changed |= DrawMaterialEffects(decal);
            changed |= DrawPlacementSettings(dTexture, decal);

            if (changed)
            {
                if (decal.Surface)
                    MarkSurfacePreviewDirty();
                Save(dTexture);
            }

            if (ImUtf8.SmallButton("Remove"u8))
                remove = idx;
            ImGui.SameLine();
            if (ImUtf8.SmallButton("Up"u8) && idx > 0)
                swap = (idx, idx - 1);
            ImGui.SameLine();
            if (ImUtf8.SmallButton("Down"u8) && idx < layers.Count - 1)
                swap = (idx, idx + 1);
        }

        if (remove >= 0)
        {
            if (layers[remove] is DecalLayer removedDecal)
                CleanupSlotEdits(dTexture, removedDecal);
            layers.RemoveAt(remove);
            if (layers.Count == 0)
                dTexture.Data.Textures.Remove(_selectedTexture);
            Save(dTexture);
        }
        else if (swap.Item1 >= 0)
        {
            (layers[swap.Item1], layers[swap.Item2]) = (layers[swap.Item2], layers[swap.Item1]);
            Save(dTexture);
        }
    }

    /// <summary>
    /// A colorset decal renders through automatically claimed colorset rows: the image is
    /// quantized to at most Max Colors and each extracted color gets its own free row — A
    /// and B halves of a pair serve as two independent colors. This editor bundles the
    /// color list, dye behavior and shape threshold — the one place all colorset settings live.
    /// </summary>
    private bool DrawIdRemapSettings(DTexture dTexture, DecalLayer decal)
    {
        var option = _options?.Find(o => string.Equals(o.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase));
        if (option?.Mtrl.Table is not ColorTable table)
            return false;

        EnsureIdStats(dTexture);
        var changed = false;

        // Old saves and layers whose allocation was cleared claim their rows on first draw.
        // A pair shared with another decal (possible in saves from the row-granular scheme)
        // fringes at decal edges — heal it by reallocating onto fully owned pairs.
        if (decal is { Enabled: true, RowError: null })
        {
            var conflict = decal.PaletteRows.Count > 0
             && ClaimedRowsForMaterial(dTexture, option.MaterialGamePath, decal) is var otherRows
             && decal.PaletteRows.Any(otherRows.Contains);
            if (decal.PaletteRows.Count == 0 || conflict)
                changed |= ReallocateDecal(dTexture, option, table, decal);
        }

        ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
        var maxColors = decal.MaxColors;
        if (ImUtf8.Slider("Max Colors"u8, ref maxColors, "%d"u8, 1, 12))
            decal.MaxColors = Math.Clamp(maxColors, 1, 12);
        if (ImGui.IsItemDeactivatedAfterEdit())
            changed |= ReallocateDecal(dTexture, option, table, decal);
        ImUtf8.HoverTooltip(
            "The decal is reduced to at most this many colors — similar colors merge.\nColors claim whole free colorset slots, two colors per slot (its A and B rows), so 6 colors need 3 fully free slots.\nSlots are never shared between decals — the game blends a slot's A and B rows at every decal edge, so foreign colors would fringe."u8);

        ImGui.SameLine();
        if (ImUtf8.SmallButton("Re-extract Colors"u8))
            changed |= ReallocateDecal(dTexture, option, table, decal);
        ImUtf8.HoverTooltip("Quantize the decal image again and reassign rows — discards manual recolors below."u8);

        if (decal.RowError != null)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, 0xFF00A0FFu);
            ImUtf8.TextWrapped(decal.RowError);
            return changed;
        }

        if (decal.PaletteRows.Count == 0)
            return changed;

        var edit = GetOrAddMaterialEdit(dTexture, option);
        var rows = decal.PaletteRows.Select(r => GetOrSeedRow(edit, table, r)).ToList();

        // The decal owns whole pairs; an odd color count leaves a shade-partner half that
        // follows the decal's dye and reset behavior without being an editable color.
        var claimedIndices = decal.PaletteRows.SelectMany(r => new[] { r, r ^ 1 }).Distinct()
            .Where(r => edit.Rows.ContainsKey(r)).ToList();
        var claimedRows = claimedIndices.Select(r => edit.Rows[r]).ToList();

        // One editable color per claimed row; the extracted swatch stays as reference so
        // recoloring never loses which image color the row renders.
        for (var i = 0; i < decal.PaletteRows.Count; ++i)
        {
            using var id       = ImUtf8.PushId(i);
            var       row      = decal.PaletteRows[i];
            var       rowEdit  = rows[i];
            var       source   = i < decal.PaletteColors.Count ? new Rgba32(decal.PaletteColors[i]) : new Rgba32(255, 255, 255);

            ImGui.ColorButton("##extracted", new Vector4(source.R / 255f, source.G / 255f, source.B / 255f, 1f));
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("The color extracted from the decal image — image pixels closest to it render through this row."u8);

            ImGui.SameLine();
            var color = new Vector3(rowEdit.Diffuse[0], rowEdit.Diffuse[1], rowEdit.Diffuse[2]);
            ImGui.SetNextItemWidth(250 * ImUtf8.GlobalScale);
            if (ImUtf8.ColorEdit($"Row {RowName(row)}", ref color, ImGuiColorEditFlags.Float))
            {
                rowEdit.Diffuse = [color.X, color.Y, color.Z];
                changed         = true;
            }

            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("This part of the decal renders in this color — recolor it without touching the image."u8);

            ImGui.SameLine();
            ImUtf8.IconButton(Dalamud.Interface.FontAwesomeIcon.Eye,
                "Highlights the parts of the model this row colors while hovered (redraws your character).\nAfter a build, that includes the decal itself."u8);
            if (ImGui.IsItemHovered())
            {
                _highlightHovered = true;
                highlighter.Highlight(option.MaterialGamePath, option.Mtrl, row);
            }
        }

        if (ImUtf8.SmallButton("Reset Rows"u8))
        {
            // Re-seed the claimed rows from an authored source row, keeping only the colors —
            // recovers rows that carry stale or filler values from earlier edits.
            foreach (var row in claimedIndices)
            {
                var keep = edit.Rows.TryGetValue(row, out var old) ? old.Diffuse : null;
                edit.Rows.Remove(row);
                var seeded = GetOrSeedRow(edit, table, row);
                if (keep != null)
                    seeded.Diffuse = keep;
            }

            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImUtf8.HoverTooltip("Rebuild the claimed rows from the gear's own authored values (keeps your colors).\nUse this after plugin updates or if the decal renders black or washed out from older edits."u8);

        // Dye: one switch across all claimed rows with a smart default copied from how the
        // rest of the gear dyes.
        var lead    = rows[0];
        var dyeable = lead.DyeMode == ColorRowEdit.RowDyeMode.Custom;
        if (ImUtf8.Checkbox("Dyeable"u8, ref dyeable))
        {
            if (dyeable)
            {
                var garmentDye = DetectGarmentDye(option.Mtrl);
                foreach (var row in claimedRows)
                {
                    row.DyeMode = ColorRowEdit.RowDyeMode.Custom;
                    if (garmentDye is { } dye)
                    {
                        row.DyeTemplate  = dye.Template;
                        row.DyeChannel   = dye.Channel;
                        row.DyeDiffuse   = dye.Flags.DiffuseColor;
                        row.DyeSpecular  = dye.Flags.SpecularColor;
                        row.DyeEmissive  = dye.Flags.EmissiveColor;
                        row.DyeRoughness = dye.Flags.Roughness;
                        row.DyeMetalness = dye.Flags.Metalness;
                        row.DyeSheen     = dye.Flags.SheenRate;
                    }
                    else
                    {
                        row.DyeDiffuse = true;
                    }
                }
            }
            else
            {
                foreach (var row in claimedRows)
                    row.DyeMode = ColorRowEdit.RowDyeMode.Disable;
            }

            changed = true;
        }

        if (dyeable)
        {
            ImGui.SameLine();
            var channel = lead.DyeChannel + 1;
            ImGui.SetNextItemWidth(100 * ImUtf8.GlobalScale);
            if (ImUtf8.Slider("Dye Channel"u8, ref channel, "%d"u8, 1, 2))
            {
                foreach (var row in claimedRows)
                    row.DyeChannel = (byte)(channel - 1);
                changed = true;
            }

            ImGui.SameLine();
            var template = (int)lead.DyeTemplate;
            ImGui.SetNextItemWidth(100 * ImUtf8.GlobalScale);
            if (ImUtf8.InputScalar("Dye Template"u8, ref template) && template is >= 0 and <= 2047)
            {
                foreach (var row in claimedRows)
                    row.DyeTemplate = (ushort)template;
                changed = true;
            }

            ImUtf8.HoverTooltip(
                "How stain colors translate to the claimed rows — detected from how the rest of this gear dyes.\nIf it reads 0, no template was detected; copy the id from a similar dyeable item."u8);

            ImUtf8.Text(lead.DyeTemplate > 0
                ? $"Dyes like the rest of this gear (template {lead.DyeTemplate})."
                : "No dye template detected on this gear — the decal will not react to dyes until a template id is set above.");
        }
        else
        {
            ImUtf8.Text("The decal keeps its colors when the gear is dyed."u8);
        }

        ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
        changed |= ImUtf8.Slider("Shape Threshold"u8, ref decal.AlphaThreshold, "%.2f"u8, 0.05f, 1f);
        if (ImGui.IsItemDeactivatedAfterEdit())
            changed |= ReallocateDecal(dTexture, option, table, decal);
        ImUtf8.HoverTooltip("Decal pixels whose alpha is at or above this value become part of the stamped shape.\nChanging it re-extracts the colors."u8);

        if (changed)
        {
            _slotPreviewDirty  = true;
            _slotPreviewMs     = Environment.TickCount64;
            _slotPreviewOption = option;
        }

        return changed;
    }

    /// <summary>
    /// Quantize the decal image and claim one free colorset row per extracted color. Rows
    /// the gear renders or other decals claim stay untouched; when not enough rows are free
    /// the layer is disabled with an error until rows free up or Max Colors shrinks.
    /// </summary>
    private bool ReallocateDecal(DTexture dTexture, TextureOption option, ColorTable table, DecalLayer decal)
    {
        var path = decals.FilePath(decal.DecalId);
        if (!File.Exists(path))
            return false;

        EnsureIdStats(dTexture);
        var edit   = GetOrAddMaterialEdit(dTexture, option);
        var others = ClaimedRowsForMaterial(dTexture, option.MaterialGamePath, decal);

        // Release the previous claim (whole pairs, including shade partners) first; rows
        // other layers still use stay.
        foreach (var row in decal.PaletteRows.SelectMany(r => new[] { r, r ^ 1 }).Distinct().Where(r => !others.Contains(r)))
            edit.Rows.Remove(row);
        decal.PaletteRows.Clear();

        uint[] palette;
        try
        {
            palette = DecalQuantizer.ExtractPalette(path, decal.MaxColors, decal.AlphaThreshold);
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Failed to quantize decal {decal.DecalId}: {ex}");
            palette = [];
        }

        decal.PaletteColors = palette.ToList();
        if (palette.Length == 0)
        {
            decal.RowError = "Could not extract any colors from the decal image — is the shape threshold too high?";
            decal.Enabled  = false;
        }
        else
        {
            var result = ColorRowAllocator.Allocate(palette.Length, _usedRowPairs, others);
            decal.RowError = result.Error;
            if (result.Success)
            {
                decal.PaletteRows = result.Rows;
                for (var i = 0; i < result.Rows.Count; ++i)
                {
                    var color = new Rgba32(palette[i]);
                    edit.Rows.Remove(result.Rows[i]);
                    GetOrSeedRow(edit, table, result.Rows[i]).Diffuse = [color.R / 255f, color.G / 255f, color.B / 255f];
                }

                // An odd color count leaves the last pair's B half unused — seed it as a
                // darkened shade of its A partner so edge texels blending toward it (the id
                // map's G channel always mixes a pair's halves) darken instead of fringing.
                if (result.Rows.Count % 2 == 1)
                {
                    var last = new Rgba32(palette[^1]);
                    var shadeRow = result.Rows[^1] ^ 1;
                    edit.Rows.Remove(shadeRow);
                    GetOrSeedRow(edit, table, shadeRow).Diffuse =
                        [last.R / 255f * ShadeFactor, last.G / 255f * ShadeFactor, last.B / 255f * ShadeFactor];
                }
            }
            else
            {
                decal.Enabled = false;
            }
        }

        if (edit.IsEmpty)
            dTexture.Data.Materials.Remove(option.MaterialGamePath);

        return true;
    }

    /// <summary>
    /// All colorset rows claimed by colorset decals on any texture of this material. A decal
    /// owns the WHOLE pair of every row it renders — the pair's other half either renders
    /// another of its colors or carries its shade partner, and must never go to another decal
    /// (the id map's G channel blends the two halves at every edge texel).
    /// </summary>
    private HashSet<int> ClaimedRowsForMaterial(DTexture dTexture, string materialGamePath, DecalLayer? except)
    {
        var ret = new HashSet<int>();
        foreach (var (gamePath, layers) in dTexture.Data.Textures)
        {
            var opt = _options?.Find(o => string.Equals(o.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
            if (opt == null || !string.Equals(opt.MaterialGamePath, materialGamePath, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var layer in layers.OfType<DecalLayer>())
                if (layer.IdRemap && !ReferenceEquals(layer, except))
                    foreach (var row in layer.PaletteRows)
                    {
                        ret.Add(row);
                        ret.Add(row ^ 1);
                    }
        }

        return ret;
    }

    /// <summary>
    /// All textures of a material are related: a decal can also smooth the normal map and
    /// set a surface finish on the mask map inside its footprint. Off by default; only
    /// offered when the material actually has those sibling textures.
    /// </summary>
    private bool DrawMaterialEffects(DecalLayer decal)
    {
        var option = _options?.Find(o => string.Equals(o.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase));
        if (option == null)
            return false;

        var hasNormal = _options!.Any(o => string.Equals(o.MaterialGamePath, option.MaterialGamePath, StringComparison.OrdinalIgnoreCase)
         && o.Slot is TextureSlot.Normal);
        var hasMask = _options!.Any(o => string.Equals(o.MaterialGamePath, option.MaterialGamePath, StringComparison.OrdinalIgnoreCase)
         && o.Slot is TextureSlot.Mask);
        if (!hasNormal && !hasMask)
            return false;

        var changed = false;
        ImGui.Separator();
        ImUtf8.Text("Material Effects"u8);
        ImUtf8.HoverTooltip("The decal's footprint replayed onto the material's other textures — smoothing bump detail or changing the surface finish under the decal."u8);

        if (hasNormal)
        {
            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            var smooth = decal.NormalSmooth;
            if (ImUtf8.Slider("Normal Smoothing"u8, ref smooth, "%.2f"u8, 0f, 1f))
            {
                decal.NormalSmooth = Math.Clamp(smooth, 0f, 1f);
                changed            = true;
            }

            ImUtf8.HoverTooltip("Flattens the cloth/skin bump detail under the decal — like a print sitting on top of the fabric.\n0 leaves the normal map untouched."u8);
        }

        if (hasMask)
        {
            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            using (var combo = ImUtf8.Combo("Surface Finish"u8, MaskPresetLabel(decal.MaskPreset)))
            {
                if (combo)
                    foreach (var preset in Enum.GetValues<DecalMaskPreset>())
                    {
                        if (!ImUtf8.Selectable(MaskPresetLabel(preset), preset == decal.MaskPreset) || preset == decal.MaskPreset)
                            continue;

                        decal.MaskPreset = preset;
                        changed          = true;
                    }
            }

            ImUtf8.HoverTooltip("How the surface responds to light under the decal, written into the material's mask map.\nMatte suits cloth prints, Glossy suits stickers/vinyl. Experimental — mask channel meanings are still being verified."u8);
        }

        if (decal.HasMaterialEffects)
        {
            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            var effectScale = decal.EffectScale;
            if (ImUtf8.Slider("Effect Scale"u8, ref effectScale, "%.2f"u8, 0.25f, 3f))
            {
                decal.EffectScale = Math.Clamp(effectScale, 0.25f, 3f);
                changed           = true;
            }

            ImUtf8.HoverTooltip("Size of the affected area relative to the decal — above 1 the smoothing/finish extends past the decal's edge, below 1 it stays inside it."u8);
        }

        return changed;
    }

    private static string MaskPresetLabel(DecalMaskPreset preset)
        => preset switch
        {
            DecalMaskPreset.Matte  => "Matte",
            DecalMaskPreset.Glossy => "Glossy",
            _                      => "Keep",
        };

    private MaterialEdit GetOrAddMaterialEdit(DTexture dTexture, TextureOption option)
    {
        if (dTexture.Data.Materials.TryGetValue(option.MaterialGamePath, out var edit))
            return edit;

        edit = new MaterialEdit { ShaderName = option.Mtrl.ShaderPackage.Name };
        dTexture.Data.Materials[option.MaterialGamePath] = edit;
        return edit;
    }

    private ColorRowEdit GetOrSeedRow(MaterialEdit edit, ColorTable table, int rowIndex)
    {
        if (edit.Rows.TryGetValue(rowIndex, out var row))
            return row;

        var seeded = ColorRowEdit.FromRow(rowIndex, table[SeedTemplateIndex(table, rowIndex)]);
        seeded.RowIndex = rowIndex;
        // Deterministic default for claimed slots: the decal keeps its color unless the
        // user explicitly makes it dyeable — inheriting the template row's dye entry would
        // silently let an applied stain override the picked color.
        seeded.DyeMode      = ColorRowEdit.RowDyeMode.Disable;
        edit.Rows[rowIndex] = seeded;
        return seeded;
    }

    /// <summary>
    /// The source row a claimed slot copies its non-color values from. Unused filler rows
    /// render BLACK in-game despite their white diffuse, so seeding must always start from
    /// an authored row: the slot's own row when the garment author populated it, a B row's
    /// own A partner, else the authored row the id map actually renders the most.
    /// </summary>
    private int SeedTemplateIndex(ColorTable table, int rowIndex)
    {
        if (!IsFillerRow(table[rowIndex]))
            return rowIndex;

        // A filler B row blends with its pair's A row — that A row is the pair's look.
        if (rowIndex % 2 == 1 && !IsFillerRow(table[rowIndex - 1]))
            return rowIndex - 1;

        foreach (var (idx, _) in _rowUsageCounts.OrderByDescending(kvp => kvp.Value))
            if (idx >= 0 && idx < ColorTable.NumRows && !IsFillerRow(table[idx]))
                return idx;

        for (var i = 0; i < ColorTable.NumRows; ++i)
            if (!IsFillerRow(table[i]))
                return i;

        return rowIndex;
    }

    /// <summary> The signature of an untouched colorset row: white diffuse/specular, legacy gloss 20, default tile transform. </summary>
    private static bool IsFillerRow(in ColorTableRow row)
        => (float)row.DiffuseColor.Red == 1f && (float)row.DiffuseColor.Green == 1f && (float)row.DiffuseColor.Blue == 1f
        && (float)row.SpecularColor.Red == 1f && (float)row.SpecularColor.Green == 1f && (float)row.SpecularColor.Blue == 1f
        && (float)row.Scalar3 == 20f
        && (float)row.Roughness == 0f
        && (float)row.TileTransform.UU == 16f && (float)row.TileTransform.VV == 16f;

    /// <summary> Removing a colorset-decal layer releases its claimed row edits unless another layer still uses them. </summary>
    private void CleanupSlotEdits(DTexture dTexture, DecalLayer removed)
    {
        if (!removed.IdRemap || removed.PaletteRows.Count == 0)
            return;

        var option = _options?.Find(o => string.Equals(o.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase));
        if (option == null || !dTexture.Data.Materials.TryGetValue(option.MaterialGamePath, out var edit))
            return;

        var others = ClaimedRowsForMaterial(dTexture, option.MaterialGamePath, removed);
        foreach (var row in removed.PaletteRows.SelectMany(r => new[] { r, r ^ 1 }).Distinct().Where(r => !others.Contains(r)))
            edit.Rows.Remove(row);
        if (edit.IsEmpty)
            dTexture.Data.Materials.Remove(option.MaterialGamePath);
    }

    /// <summary> The dye behavior most of this gear uses: the most frequent dye entry of the source material. </summary>
    private static (ushort Template, byte Channel, ColorDyeTableRow Flags)? DetectGarmentDye(MtrlFile mtrl)
    {
        if (mtrl.DyeTable is not ColorDyeTable dyeTable)
            return null;

        var counts = new Dictionary<ushort, (int Count, int Row)>();
        for (var i = 0; i < ColorDyeTable.NumRows; ++i)
        {
            var template = dyeTable[i].Template;
            if (template == 0)
                continue;

            counts[template] = counts.TryGetValue(template, out var existing) ? (existing.Count + 1, existing.Row) : (1, i);
        }

        if (counts.Count == 0)
            return null;

        var best = counts.OrderByDescending(kvp => kvp.Value.Count).First();
        var row  = dyeTable[best.Value.Row];
        return (best.Key, row.Channel, row);
    }

    private enum DragMode
    {
        None,
        Move,
        Scale,
        Rotate,
    }

    private int      _activeLayer = -1;
    private DragMode _dragMode    = DragMode.None;
    private Vector2  _grabOffset;
    private float    _rotateGrab;
    private Vector2  _scaleSign = Vector2.One;

    private readonly record struct LayerGeometry(Vector2 Center, Vector2 Half, float Sin, float Cos)
    {
        public Vector2 ToScreen(Vector2 local)
            => Center + new Vector2(local.X * Cos - local.Y * Sin, local.X * Sin + local.Y * Cos);

        public Vector2 ToLocal(Vector2 screen)
        {
            var d = screen - Center;
            return new Vector2(d.X * Cos + d.Y * Sin, -d.X * Sin + d.Y * Cos);
        }

        public Vector2 RotationHandle
            => ToScreen(new Vector2(0, -Half.Y - 22));

        public bool Contains(Vector2 screen)
        {
            var local = ToLocal(screen);
            return Math.Abs(local.X) <= Half.X && Math.Abs(local.Y) <= Half.Y;
        }
    }

    private static LayerGeometry Geometry(DecalLayer layer, Vector2 start, Vector2 size)
    {
        var center = start + new Vector2(layer.PosU * size.X, layer.PosV * size.Y);
        var half   = new Vector2(Math.Max(2f, layer.ScaleX * size.X), Math.Max(2f, layer.ScaleY * size.Y)) / 2;
        var (sin, cos) = MathF.SinCos(layer.RotationDeg * MathF.PI / 180f);
        return new LayerGeometry(center, half, sin, cos);
    }

    /// <summary>
    /// In-window preview: the base texture with decal layers drawn on top, directly
    /// manipulable — drag the decal to move it, corner handles to resize, top handle to rotate.
    /// </summary>
    private void DrawPreview(DTexture dTexture)
    {
        var wrap = GetPreviewWrap(dTexture);
        if (wrap == null)
        {
            ImUtf8.Text("No preview available for this texture."u8);
            return;
        }

        // Surface decals are baked into the preview image itself (debounced).
        UpdateSurfacePreview(dTexture);
        wrap = _previewWrap ?? wrap;

        ImUtf8.Text("Click a decal to select it, drag to move, corner handles to resize (Shift: keep aspect), top handle to rotate (Ctrl: snap)."u8);

        var showSeams = config.ShowUvSeams;
        if (ImUtf8.Checkbox("Show UV Seams"u8, ref showSeams))
        {
            config.ShowUvSeams = showSeams;
            config.Save();
        }

        ImUtf8.HoverTooltip("Outlines where the model's UV islands end.\nA decal crossing one of these lines gets cut off there on the actual gear."u8);

        var layout = showSeams ? GetUvLayout(dTexture) : null;
        if (showSeams)
        {
            ImGui.SameLine();
            if (layout != null)
                ImUtf8.Text($"({layout.Seams.Count} seam edges)");
            else
                using (ImRaii.PushColor(ImGuiCol.Text, 0xFF00A0FFu))
                    ImUtf8.Text("no UV data for this texture — see /xllog, or re-add the material in the Source tab"u8);
        }

        var avail = ImGui.GetContentRegionAvail().X;
        var scale = Math.Min(1f, avail / wrap.Width);
        var size  = new Vector2(wrap.Width * scale, wrap.Height * scale);
        var start = ImGui.GetCursorScreenPos();

        // An invisible button owns the canvas so dragging never moves the window.
        ImUtf8.InvisibleButton("##previewCanvas"u8, size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddImage(wrap.Handle, start, start + size);

        if (layout != null)
        {
            // Dark underlay below each bright line keeps the seams readable on any texture.
            const uint seamOutline = 0xC0000000u;
            const uint seamColor   = 0xFF00D7FFu;
            foreach (var (a, b) in layout.Seams)
                drawList.AddLine(start + a * size, start + b * size, seamOutline, 3f);
            foreach (var (a, b) in layout.Seams)
                drawList.AddLine(start + a * size, start + b * size, seamColor, 1.5f);
        }

        if (!dTexture.Data.Textures.TryGetValue(_selectedTexture, out var allLayers))
            return;

        // Surface decals live in the baked preview image and are placed on the model —
        // only flat UV decals are drawn as directly manipulable rectangles here.
        var layers = allLayers.OfType<DecalLayer>().Where(l => !l.Surface).ToList();
        if (_activeLayer >= layers.Count)
            _activeLayer = -1;

        foreach (var layer in layers.Where(l => l.Enabled))
        {
            var decalWrap = textureProvider.GetFromFile(decals.FilePath(layer.DecalId)).GetWrapOrDefault();
            if (decalWrap == null)
                continue;

            var geo = Geometry(layer, start, size);
            // Colorset decals get a flat orange tint in the preview — their real color comes
            // from the target row pair, which the base ID map cannot show.
            var tint = layer.IdRemap
                ? 0xFF20A5FFu
                : (uint)(Math.Clamp(layer.Opacity, 0f, 1f) * 255f) << 24 | 0x00FFFFFFu;
            drawList.AddImageQuad(decalWrap.Handle,
                geo.ToScreen(new Vector2(-geo.Half.X, -geo.Half.Y)), geo.ToScreen(new Vector2(geo.Half.X, -geo.Half.Y)),
                geo.ToScreen(new Vector2(geo.Half.X, geo.Half.Y)), geo.ToScreen(new Vector2(-geo.Half.X, geo.Half.Y)),
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
                tint);
        }

        HandleManipulation(dTexture, layers, start, size, drawList);
    }

    private void HandleManipulation(DTexture dTexture, List<DecalLayer> layers, Vector2 start, Vector2 size, ImDrawListPtr drawList)
    {
        var mouse = ImGui.GetMousePos();

        if (ImGui.IsItemActivated())
            BeginDrag(layers, start, size, mouse);

        if (_dragMode != DragMode.None)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || _activeLayer < 0 || _activeLayer >= layers.Count)
            {
                _dragMode = DragMode.None;
                Save(dTexture);
            }
            else
            {
                UpdateDrag(layers[_activeLayer], start, size, mouse);
            }
        }

        // Selection visuals: outline + handles for the active layer.
        if (_activeLayer < 0 || _activeLayer >= layers.Count)
            return;

        var active = layers[_activeLayer];
        var geo    = Geometry(active, start, size);
        var corners = new[]
        {
            geo.ToScreen(new Vector2(-geo.Half.X, -geo.Half.Y)),
            geo.ToScreen(new Vector2(geo.Half.X, -geo.Half.Y)),
            geo.ToScreen(new Vector2(geo.Half.X, geo.Half.Y)),
            geo.ToScreen(new Vector2(-geo.Half.X, geo.Half.Y)),
        };

        const uint outlineColor = 0xFF00D7FFu;
        for (var i = 0; i < 4; ++i)
            drawList.AddLine(corners[i], corners[(i + 1) % 4], outlineColor, 1.5f);
        foreach (var corner in corners)
            drawList.AddRectFilled(corner - new Vector2(4), corner + new Vector2(4), outlineColor);

        var rotHandle = geo.RotationHandle;
        drawList.AddLine(geo.ToScreen(new Vector2(0, -geo.Half.Y)), rotHandle, outlineColor, 1.5f);
        drawList.AddCircleFilled(rotHandle, 6f, outlineColor);
    }

    private void BeginDrag(List<DecalLayer> layers, Vector2 start, Vector2 size, Vector2 mouse)
    {
        // Handles of the already-selected layer win over selecting another layer.
        if (_activeLayer >= 0 && _activeLayer < layers.Count && layers[_activeLayer].Enabled)
        {
            var geo = Geometry(layers[_activeLayer], start, size);
            if (Vector2.Distance(mouse, geo.RotationHandle) <= 10f)
            {
                _dragMode   = DragMode.Rotate;
                var local   = mouse - geo.Center;
                _rotateGrab = MathF.Atan2(local.Y, local.X) - layers[_activeLayer].RotationDeg * MathF.PI / 180f;
                return;
            }

            foreach (var (sx, sy) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                var corner = geo.ToScreen(new Vector2(sx * geo.Half.X, sy * geo.Half.Y));
                if (Vector2.Distance(mouse, corner) <= 8f)
                {
                    _dragMode  = DragMode.Scale;
                    _scaleSign = new Vector2(sx, sy);
                    return;
                }
            }
        }

        // Select and start moving the topmost decal under the cursor.
        for (var i = layers.Count - 1; i >= 0; --i)
        {
            if (!layers[i].Enabled)
                continue;

            var geo = Geometry(layers[i], start, size);
            if (!geo.Contains(mouse))
                continue;

            _activeLayer = i;
            _dragMode    = DragMode.Move;
            _grabOffset  = mouse - geo.Center;
            return;
        }

        _activeLayer = -1;
    }

    private void UpdateDrag(DecalLayer layer, Vector2 start, Vector2 size, Vector2 mouse)
    {
        switch (_dragMode)
        {
            case DragMode.Move:
            {
                var center = mouse - _grabOffset - start;
                layer.PosU = Math.Clamp(center.X / size.X, 0f, 1f);
                layer.PosV = Math.Clamp(center.Y / size.Y, 0f, 1f);
                break;
            }
            case DragMode.Rotate:
            {
                var local = mouse - (start + new Vector2(layer.PosU * size.X, layer.PosV * size.Y));
                var deg   = (MathF.Atan2(local.Y, local.X) - _rotateGrab) * 180f / MathF.PI;
                if (ImGui.GetIO().KeyCtrl)
                    deg = MathF.Round(deg / 15f) * 15f;
                layer.RotationDeg = deg switch
                {
                    > 180f  => deg - 360f,
                    < -180f => deg + 360f,
                    _       => deg,
                };
                break;
            }
            case DragMode.Scale:
            {
                var geo   = Geometry(layer, start, size);
                var local = geo.ToLocal(mouse);
                var newScaleX = Math.Clamp(Math.Abs(local.X) * 2f / size.X, 0.01f, 2f);
                var newScaleY = Math.Clamp(Math.Abs(local.Y) * 2f / size.Y, 0.01f, 2f);
                if (ImGui.GetIO().KeyShift && layer.ScaleX > 0.001f && layer.ScaleY > 0.001f)
                {
                    // Keep aspect: scale both axes by the dominant factor.
                    var factor = Math.Max(newScaleX / layer.ScaleX, newScaleY / layer.ScaleY);
                    layer.ScaleX = Math.Clamp(layer.ScaleX * factor, 0.01f, 2f);
                    layer.ScaleY = Math.Clamp(layer.ScaleY * factor, 0.01f, 2f);
                }
                else
                {
                    layer.ScaleX = newScaleX;
                    layer.ScaleY = newScaleY;
                }

                break;
            }
        }
    }

    private bool _mdlHealAttempted;

    /// <summary>
    /// The source material of the selected texture. Sources saved before model paths were
    /// captured are healed once per selection through a live resource-tree resolve.
    /// </summary>
    private SourcePath? FindSelectedSource(DTexture dTexture)
    {
        var option = _options?.Find(o => string.Equals(o.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase));
        if (option == null)
            return null;

        var source = dTexture.Data.Source.Materials.FirstOrDefault(m
            => string.Equals(m.GamePath, option.MaterialGamePath, StringComparison.OrdinalIgnoreCase));
        if (source == null)
            return null;

        if (source.MdlGamePath.Length == 0 && !_mdlHealAttempted)
        {
            _mdlHealAttempted = true;
            try
            {
                var live = resolver.ResolvePlayer()
                    .SelectMany(g => g.Materials)
                    .FirstOrDefault(m => string.Equals(m.GamePath, source.GamePath, StringComparison.OrdinalIgnoreCase));
                if (live is { MdlGamePath.Length: > 0 })
                {
                    source.MdlGamePath   = live.MdlGamePath;
                    source.MdlActualPath = live.MdlActualPath;
                    saveService.QueueSave(dTexture);
                }
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Warning($"Could not recover the model path of {source.GamePath}: {ex.Message}");
            }
        }

        return source;
    }

    private UvLayout? GetUvLayout(DTexture dTexture)
    {
        var source = FindSelectedSource(dTexture);
        return source == null ? null : uvReader.Get(source);
    }

    #region Surface placement

    private string _placementError = string.Empty;

    /// <summary> Placement controls of one decal layer: flat UV stamping or 3D surface projection. </summary>
    private bool DrawPlacementSettings(DTexture dTexture, DecalLayer decal)
    {
        var changed = false;
        var surface = decal.Surface;
        if (ImUtf8.Checkbox("Place on Model (3D)"u8, ref surface))
        {
            decal.Surface = surface;
            changed       = true;
            MarkSurfacePreviewDirty();
            // Entering 3D mode keeps the decal where it is: the current UV position is
            // converted to a mesh anchor. Only when that fails does placement mode open.
            if (surface && decal is { AnchorX: 0f, AnchorY: 0f, AnchorZ: 0f })
            {
                var source = FindSelectedSource(dTexture);
                var mesh   = source == null ? null : uvReader.GetMesh(source);
                if (mesh == null || !SeedSurfaceFromUv(mesh, decal))
                    OpenViewport(dTexture, decal);
            }
        }

        ImUtf8.HoverTooltip(
            "Project the decal onto the 3D mesh instead of stamping it flat into the texture.\nIt conforms to the surface, keeps a real-world size and continues across UV seams."u8);

        if (decal.Surface)
        {
            var widthCm = decal.WorldWidth * 100f;
            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            if (ImUtf8.Slider("Width (cm)"u8, ref widthCm, "%.1f"u8, 1f, 100f))
            {
                decal.WorldWidth = widthCm / 100f;
                changed          = true;
            }

            var heightCm = decal.WorldHeight * 100f;
            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            if (ImUtf8.Slider("Height (cm)"u8, ref heightCm, "%.1f"u8, 1f, 100f))
            {
                decal.WorldHeight = heightCm / 100f;
                changed           = true;
            }

            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            changed |= ImUtf8.Slider("Rotation"u8, ref decal.RotationDeg, "%.1f°"u8, -180f, 180f);
            if (!decal.IdRemap)
            {
                ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
                changed |= ImUtf8.Slider("Opacity"u8, ref decal.Opacity, "%.2f"u8, 0f, 1f);
            }

            var limitToPart = decal.SurfaceLimitToPart;
            if (ImUtf8.Checkbox("Limit to Clicked Mesh Part"u8, ref limitToPart))
            {
                decal.SurfaceLimitToPart = limitToPart;
                changed                  = true;
            }

            ImUtf8.HoverTooltip(
                "Keep the projection on the mesh piece you stamped it on.\nWithout this, overlapping pieces (linings, straps, panels behind) within reach catch the decal too."u8);

            if (ImUtf8.Button(_viewport.IsOpenFor(decal) ? "Close 3D View"u8 : "Place in 3D View..."u8))
            {
                if (_viewport.IsOpenFor(decal))
                    _viewport.Close();
                else
                    OpenViewport(dTexture, decal);
            }

            ImUtf8.HoverTooltip(
                "Opens a window rendering the gear mesh in 3D — stamp and drag the decal directly on it,\norbit and zoom freely, and changes apply to the mod when you finish an adjustment."u8);

            if (_placementError.Length > 0)
                using (ImRaii.PushColor(ImGuiCol.Text, 0xFF00A0FFu))
                    ImUtf8.Text(_placementError);
            else if (decal is { AnchorX: 0f, AnchorY: 0f, AnchorZ: 0f })
                using (ImRaii.PushColor(ImGuiCol.Text, 0xFF00A0FFu))
                    ImUtf8.Text("NOT PLACED YET — the decal stays invisible until you place it in the 3D View."u8);
        }
        else
        {
            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            changed |= ImUtf8.Slider("Position U"u8, ref decal.PosU, "%.3f"u8, 0f, 1f);
            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            changed |= ImUtf8.Slider("Position V"u8, ref decal.PosV, "%.3f"u8, 0f, 1f);
            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            changed |= ImUtf8.Slider("Scale X"u8, ref decal.ScaleX, "%.3f"u8, 0.01f, 1f);
            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            changed |= ImUtf8.Slider("Scale Y"u8, ref decal.ScaleY, "%.3f"u8, 0.01f, 1f);
            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            changed |= ImUtf8.Slider("Rotation"u8, ref decal.RotationDeg, "%.1f°"u8, -180f, 180f);
            if (!decal.IdRemap)
            {
                ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
                changed |= ImUtf8.Slider("Opacity"u8, ref decal.Opacity, "%.2f"u8, 0f, 1f);
            }
        }

        return changed;
    }

    /// <summary>
    /// Convert a decal's flat UV placement into a surface anchor so switching to 3D keeps
    /// it visually in place: find the mesh triangle under the UV center (nearest one if the
    /// center sits in empty UV space), anchor at its interpolated bind-pose position and
    /// derive the world size from the local UV density.
    /// </summary>
    private static bool SeedSurfaceFromUv(MaterialMesh mesh, DecalLayer decal)
    {
        var uv = new Vector2(decal.PosU, decal.PosV);

        static float Cross(Vector2 a, Vector2 b)
            => a.X * b.Y - a.Y * b.X;

        var bestTri  = -1;
        var bestBary = Vector3.Zero;
        var bestDist = float.MaxValue;
        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var a = mesh.Uvs[mesh.Indices[i]];
            var b = mesh.Uvs[mesh.Indices[i + 1]];
            var c = mesh.Uvs[mesh.Indices[i + 2]];
            var area = Cross(b - a, c - a);
            if (MathF.Abs(area) < 1e-9f)
                continue;

            var w0 = Cross(b - uv, c - uv) / area;
            var w1 = Cross(c - uv, a - uv) / area;
            var w2 = 1f - w0 - w1;
            if (w0 >= -0.001f && w1 >= -0.001f && w2 >= -0.001f)
            {
                bestTri  = i;
                bestBary = new Vector3(w0, w1, w2);
                break;
            }

            var dist = Vector2.DistanceSquared((a + b + c) / 3f, uv);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestTri  = i;
                bestBary = new Vector3(1f / 3f);
            }
        }

        if (bestTri < 0)
            return false;

        var i0 = mesh.Indices[bestTri];
        var i1 = mesh.Indices[bestTri + 1];
        var i2 = mesh.Indices[bestTri + 2];

        var anchor = mesh.Positions[i0] * bestBary.X + mesh.Positions[i1] * bestBary.Y + mesh.Positions[i2] * bestBary.Z;
        var normal = mesh.Normals[i0] * bestBary.X + mesh.Normals[i1] * bestBary.Y + mesh.Normals[i2] * bestBary.Z;
        if (normal.LengthSquared() < 1e-8f)
            normal = Vector3.UnitY;
        normal = Vector3.Normalize(normal);

        // World length per unit of U/V from the triangle's UV-to-position mapping — turns
        // the old texture-relative scale into an equivalent on-surface size.
        var e1   = mesh.Positions[i1] - mesh.Positions[i0];
        var e2   = mesh.Positions[i2] - mesh.Positions[i0];
        var duv1 = mesh.Uvs[i1] - mesh.Uvs[i0];
        var duv2 = mesh.Uvs[i2] - mesh.Uvs[i0];
        var det  = Cross(duv1, duv2);
        if (MathF.Abs(det) > 1e-9f)
        {
            var dPdu = (e1 * duv2.Y - e2 * duv1.Y) / det;
            var dPdv = (e2 * duv1.X - e1 * duv2.X) / det;
            decal.WorldWidth  = Math.Clamp(decal.ScaleX * dPdu.Length(), 0.005f, 2f);
            decal.WorldHeight = Math.Clamp(decal.ScaleY * dPdv.Length(), 0.005f, 2f);
        }

        decal.AnchorX     = anchor.X;
        decal.AnchorY     = anchor.Y;
        decal.AnchorZ     = anchor.Z;
        decal.NormalX     = normal.X;
        decal.NormalY     = normal.Y;
        decal.NormalZ     = normal.Z;
        decal.SurfacePart = mesh.TriangleParts[bestTri / 3];
        DynamicTextureManager.Log.Information(
            $"Seeded surface anchor from UV ({decal.PosU:F3}, {decal.PosV:F3}) -> ({anchor.X:F3}, {anchor.Y:F3}, {anchor.Z:F3}), size {decal.WorldWidth * 100:F1}x{decal.WorldHeight * 100:F1} cm, part {decal.SurfacePart}.");
        return true;
    }

    private void OpenViewport(DTexture dTexture, DecalLayer decal)
    {
        var source = FindSelectedSource(dTexture);

        // The worn model can differ from the one captured at selection time (e.g. another
        // size option) — re-resolve so the viewport shows the variant actually in use.
        if (source is { MdlGamePath.Length: > 0 } && penumbra.Available)
            try
            {
                var resolved  = penumbra.ResolvePlayerPath(source.MdlGamePath);
                var newActual = string.Equals(resolved, source.MdlGamePath, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : resolved;
                if (!string.Equals(newActual, source.MdlActualPath, StringComparison.OrdinalIgnoreCase))
                {
                    DynamicTextureManager.Log.Information(
                        $"Placement model re-resolved from \"{source.MdlActualPath}\" to \"{newActual}\".");
                    source.MdlActualPath = newActual;
                    saveService.QueueSave(dTexture);
                }
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Warning($"Could not re-resolve the worn model: {ex.Message}");
            }

        var mesh = source == null ? null : uvReader.GetMesh(source);
        if (mesh == null)
        {
            _placementError = "No mesh geometry available — re-add the material in the Source tab while wearing the gear.";
            return;
        }

        _placementError = string.Empty;
        _viewport.Open(dTexture, decal, mesh, modelState.CurrentAttributeMask(mesh.GamePath), decals.FilePath(decal.DecalId), () =>
        {
            MarkSurfacePreviewDirty();
            Save(dTexture);
        });
    }

    #endregion

    #region Surface preview bake

    private DecodedTexture? _previewDecoded;
    private bool            _surfaceBakeDirty;
    private long            _surfaceBakeMs;

    private void MarkSurfacePreviewDirty()
    {
        _surfaceBakeDirty = true;
        _surfaceBakeMs    = Environment.TickCount64;
    }

    /// <summary>
    /// Surface decals cannot be shown as draggable rectangles — their shape depends on the
    /// mesh. Instead the preview image itself is re-baked (debounced) with all enabled
    /// surface layers so the projected result, including seam crossings, is visible.
    /// </summary>
    private void UpdateSurfacePreview(DTexture dTexture)
    {
        if (!_surfaceBakeDirty || _previewDecoded == null)
            return;
        if (Environment.TickCount64 - _surfaceBakeMs < 600)
            return;

        _surfaceBakeDirty = false;
        var surfaceLayers = dTexture.Data.Textures.GetValueOrDefault(_selectedTexture)?
                .OfType<DecalLayer>().Where(l => l is { Enabled: true, Surface: true }).Cast<TextureLayer>().ToList()
         ?? [];

        var rgba = _previewDecoded.Rgba;
        if (surfaceLayers.Count > 0)
        {
            var source = FindSelectedSource(dTexture);
            var mesh   = source == null ? null : uvReader.GetMesh(source);
            if (mesh != null)
                rgba = compositor.Composite(_previewDecoded, surfaceLayers, mesh, previewHighlight: true);
        }

        _previewWrap?.Dispose();
        _previewWrap = textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(_previewDecoded.Width, _previewDecoded.Height),
            rgba, $"DTM Preview {_selectedTexture}");
    }

    #endregion

    private long _previewAttemptMs;

    private IDalamudTextureWrap? GetPreviewWrap(DTexture dTexture)
    {
        // Retry failed loads occasionally — the source may become recoverable (e.g. after
        // the generated mod is disabled or the source capture heals).
        if (_previewPath == _selectedTexture
         && (_previewWrap != null || Environment.TickCount64 - _previewAttemptMs < 2000))
            return _previewWrap;

        _previewWrap?.Dispose();
        _previewWrap      = null;
        _previewPath      = _selectedTexture;
        _previewAttemptMs = Environment.TickCount64;

        // The shared capture logic prefers the stored pristine source and can recover it
        // from the source mod's file lists when our own mod already owns the resolution.
        var diskPath = overlayMods.GetOrCaptureTextureSource(dTexture, _selectedTexture);
        if (diskPath is { Length: 0 })
            diskPath = null; // vanilla

        var decoded = textureIO.Load(_selectedTexture, diskPath, null);
        if (decoded == null)
            return null;

        if (_options?.Find(o => string.Equals(o.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase))?.Slot
            is TextureSlot.Index)
            ComputeIdStats(decoded);

        _previewDecoded   = decoded;
        _surfaceBakeDirty = true;
        _surfaceBakeMs    = 0;
        _previewWrap = textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(decoded.Width, decoded.Height), decoded.Rgba,
            $"DTM Preview {_selectedTexture}");
        return _previewWrap;
    }

    /// <summary>
    /// Id-map usage statistics for the selected texture: which row pairs it references and
    /// how often each row actually renders (the G channel blends a pair's A row at 255 with
    /// its B row at 0). Seeding claimed slots depends on these, so they are computed on
    /// demand instead of waiting for the preview image to load.
    /// </summary>
    private void EnsureIdStats(DTexture dTexture)
    {
        if (_statsTexture == _selectedTexture)
            return;

        var diskPath = overlayMods.GetOrCaptureTextureSource(dTexture, _selectedTexture);
        if (diskPath is { Length: 0 })
            diskPath = null; // vanilla

        var decoded = textureIO.Load(_selectedTexture, diskPath, null);
        if (decoded == null)
        {
            // Leave the stats empty but marked current — seeding falls back to the first
            // authored row, and a successful preview load recomputes them.
            _statsTexture = _selectedTexture;
            _usedRowPairs.Clear();
            _rowUsageCounts.Clear();
            return;
        }

        ComputeIdStats(decoded);
    }

    private void ComputeIdStats(DecodedTexture decoded)
    {
        _statsTexture = _selectedTexture;
        _usedRowPairs.Clear();
        _rowUsageCounts.Clear();
        for (var i = 0; i < decoded.Rgba.Length; i += 4)
        {
            var pair = Math.Clamp((int)Math.Round(decoded.Rgba[i] / 17f), 0, 15);
            _usedRowPairs.Add(pair + 1);
            var row = pair * 2 + (decoded.Rgba[i + 1] >= 128 ? 0 : 1);
            _rowUsageCounts[row] = _rowUsageCounts.GetValueOrDefault(row) + 1;
        }
    }

    private void Save(DTexture dTexture)
    {
        dTexture.LastEdit = DateTimeOffset.UtcNow;
        saveService.DelaySave(dTexture);
        overlayMods.QueueAutoApply(dTexture);
    }
}
