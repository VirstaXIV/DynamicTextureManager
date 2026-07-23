using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.UI.Panels;

/// <summary>
/// Tab for stamping decals onto the selected source materials. Selection is per MATERIAL —
/// a decal automatically targets the right texture (the colorset id map on colorset-driven
/// gear, else the diffuse) and its material effects touch the normal/mask siblings, so
/// there is no per-texture selection. The embedded 3D viewport is the main preview: it
/// renders the gear with the composited textures and the live colorset colors, and doubles
/// as the placement surface. The finished textures are viewable in the Textures tab.
/// </summary>
public sealed class DecalsTab(
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
    PenumbraService penumbra,
    CompositePreviewCache previewCache,
    FilenameService filenames,
    Configuration config,
    DecalLibraryWindow decalLibraryWindow,
    SkinColorReader skinColorReader)
    : IService, IDisposable
{
    private const long SlotPreviewDebounceMs = 400;

    /// <summary> Darkening applied to a shade-partner row: the benign blend target for a pair's unused half. </summary>
    private const float ShadeFactor = 0.6f;

    private bool           _slotPreviewDirty;
    private long           _slotPreviewMs;
    private TextureOption? _slotPreviewOption;

    private readonly FileDialogManager _fileDialog = new();

    private Guid                 _cacheOwner = Guid.Empty;
    private string               _sourceFingerprint = string.Empty;
    private List<TextureOption>? _options;
    private List<TextureOption>? _overlayOptions;
    private string               _selectedMaterial = string.Empty;
    private bool                 _highlightHovered;

    private readonly DecalViewport _viewport = new(textureProvider);

    private string                             _statsTexture = string.Empty;
    private readonly HashSet<int>              _usedRowPairs = [];
    private readonly Dictionary<int, int>      _rowUsageCounts = [];
    private readonly List<(int Row, int Count)> _sortedRowUsage = [];
    private int                                _statsTotalTexels = 1;

    private string _skinToneReadError = string.Empty;

    public void Dispose()
        => _viewport.Dispose();

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
            _overlayOptions    = null;
            _selectedMaterial  = string.Empty;
            _statsTexture      = string.Empty;
            _mdlHealAttempted  = false;
            _extractRows.Clear();
            ResetShadingState();
            _viewport.Close();
            previewer.Clear();
        }

        if (dTexture.Data.Source.IsEmpty)
        {
            ImUtf8.Text("Select a source first."u8);
            return;
        }

        // Overlay-part sources (nails, accents) are excluded here even though they're valid
        // Source.Materials entries: selecting one directly merges most of the body mesh into
        // unpaintable "context" (framed around the tiny overlay geometry) sampling the wrong
        // texture at the wrong UVs — confusing, not useful. They're painted automatically by an
        // overlapping body-skin tattoo (OverlayModManager companion bake) and stay visible
        // read-only in the Textures tab, which does NOT filter them. Their diffuse options are
        // kept separately (_overlayOptions) so the 3D viewport can still show them, composited,
        // as extra rendered entries — see BuildOverlayEntries.
        if (_options == null)
        {
            var overlayPaths = dTexture.Data.Source.Materials.Where(m => m.Overlay).Select(m => m.GamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var all = TextureOptions.Collect(dTexture.Data, sourceFiles, shaderHandlers);
            _options        = all.Where(o => !overlayPaths.Contains(o.MaterialGamePath)).ToList();
            _overlayOptions = all.Where(o => overlayPaths.Contains(o.MaterialGamePath) && o.Slot is TextureSlot.Diffuse).ToList();
        }

        if (_options.Count == 0)
        {
            ImUtf8.Text(dTexture.Data.Source.Materials.All(m => m.Overlay)
                ? "Only overlay-part sources (nails, accents) are selected — they're painted automatically by an overlapping body tattoo. Add the body itself to place one."u8
                : "The source materials expose no textures."u8);
            return;
        }

        ImGui.SetNextItemWidth(350 * ImUtf8.GlobalScale);
        TextureOptions.DrawMaterialCombo(_options, ref _selectedMaterial);
        ImGui.SameLine();
        ImUtf8.LabeledHelpMarker("Material"u8,
            "Decals work per material: they stamp onto the right texture automatically (the colorset id map on colorset-driven gear, else the color texture) and their material effects touch the normal/mask maps.\nThe finished textures are viewable in the Textures tab."u8);

        // Modern gear stamps the colorset id map; skin and legacy gear stamp the diffuse.
        // Materials exposing neither (colorset-only legacy gear, hair, vfx) stay gated off.
        if (DefaultTargetOption() == null)
        {
            var legacyIndex = MaterialOptions().Any(o => o is { Slot: TextureSlot.Index, DecalRecommended: false });
            ImUtf8.TextWrapped(legacyIndex
                ? "This material has no color texture — its look comes entirely from its colorset, which decals cannot stamp onto yet."u8
                : "This material exposes no texture decals can stamp onto."u8);
        }
        else
        {
            switch (SelectedKind())
            {
                case MaterialKind.Skin:
                {
                    ImUtf8.TextWrapped("Skin material — decals bake directly into the skin texture like tattoos and conform to the body."u8);
                    var packed = new Rgba32(config.PreviewSkinTone);
                    var tone   = new Vector3(packed.R / 255f, packed.G / 255f, packed.B / 255f);
                    ImGui.SetNextItemWidth(250 * ImUtf8.GlobalScale);
                    if (ImUtf8.ColorEdit("Preview Skin Tone"u8, ref tone, ImGuiColorEditFlags.Float))
                        config.PreviewSkinTone = new Rgba32(tone.X, tone.Y, tone.Z).PackedValue;
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        config.PreviewSkinToneUserSet = true;
                        config.Save();
                    }

                    ImUtf8.HoverTooltip(
                        "Preview-only: match your character's skin color so the 3D preview looks like your skin.\nThe game applies the real skin color in its shader — this never changes any texture."u8);

                    ImGui.SameLine();
                    if (ImUtf8.SmallButton("Use My Character's Skin Color"u8))
                    {
                        if (skinColorReader.TryGetLocalPlayerSkin(out var liveTone))
                        {
                            config.PreviewSkinTone         = new Rgba32(liveTone.X, liveTone.Y, liveTone.Z).PackedValue;
                            config.PreviewSkinToneUserSet  = true;
                            config.Save();
                        }
                        else
                        {
                            _skinToneReadError = "Could not read your character's skin color — not loaded, or not human.";
                        }
                    }

                    ImUtf8.HoverTooltip(
                        "Reads your currently loaded character's actual configured skin color from the game.\nRequires your character to be loaded and human."u8);
                    if (_skinToneReadError.Length > 0)
                        ImUtf8.TextWrapped(_skinToneReadError);
                    break;
                }
                case MaterialKind.LegacyDiffuse:
                    ImUtf8.TextWrapped("Legacy material — decal colors are baked into the color texture. Recoloring rebuilds the mod; dyes never affect the decal."u8);
                    break;
            }
        }

        ImGui.Separator();
        DrawDecalLibrary(dTexture);
        ImGui.Separator();
        DrawLayers(dTexture);
        DrawExtractionSection(dTexture);
        DrawStrayRows(dTexture);
        UpdateSlotPreview(dTexture);
        ImGui.Separator();
        DrawViewport(dTexture);
    }

    private List<TextureOption>? _materialOptionsCache;
    private (List<TextureOption>? Options, string Material) _materialOptionsKey;

    /// <summary> The selected material's options, cached — Draw paths ask for this several times per frame. </summary>
    private List<TextureOption> MaterialOptions()
    {
        if (_materialOptionsCache != null
         && ReferenceEquals(_materialOptionsKey.Options, _options)
         && string.Equals(_materialOptionsKey.Material, _selectedMaterial, StringComparison.OrdinalIgnoreCase))
            return _materialOptionsCache;

        _materialOptionsKey   = (_options, _selectedMaterial);
        _materialOptionsCache = _options?
                .Where(o => string.Equals(o.MaterialGamePath, _selectedMaterial, StringComparison.OrdinalIgnoreCase)).ToList()
         ?? [];
        return _materialOptionsCache;
    }

    /// <summary> The selected material's editing family, from its shader handler. </summary>
    private MaterialKind SelectedKind()
        => MaterialOptions().FirstOrDefault()?.Kind ?? MaterialKind.Unknown;

    /// <summary> The material's colorset id map, when the shader supports colorset decals on it. </summary>
    private TextureOption? IndexOption()
        => MaterialOptions().Find(o => o is { Slot: TextureSlot.Index, DecalRecommended: true });

    private TextureOption? DiffuseOption()
        => MaterialOptions().Find(o => o.Slot is TextureSlot.Diffuse);

    /// <summary>
    /// Where a new decal goes: modern gear prefers the colorset id map; skin, legacy and
    /// unknown materials take color decals on their diffuse.
    /// </summary>
    private TextureOption? DefaultTargetOption()
        => SelectedKind() is MaterialKind.ModernColorset ? IndexOption() ?? DiffuseOption() : DiffuseOption();

    private TextureOption? OptionFor(string gamePath)
        => _options?.Find(o => string.Equals(o.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));

    /// <summary> The texture a layer lives on, by scanning the layer stacks. </summary>
    private static string? LayerOwnerPath(DTexture dTexture, TextureLayer layer)
        => dTexture.Data.Textures.FirstOrDefault(kvp => kvp.Value.Contains(layer)).Key;

    /// <summary>
    /// Edited colorset rows on this material that no decal slot owns still affect the gear
    /// invisibly from this tab — list them so leftovers from experiments are obvious.
    /// </summary>
    private void DrawStrayRows(DTexture dTexture)
    {
        if (!dTexture.Data.Materials.TryGetValue(_selectedMaterial, out var edit) || edit.IsEmpty)
            return;

        var claimed = ClaimedRowsForMaterial(dTexture, _selectedMaterial, null);
        var strays  = edit.Rows.Keys.Where(r => !claimed.Contains(r)).OrderBy(r => r).ToList();
        if (strays.Count == 0)
            return;

        ImGui.Separator();
        ImUtf8.TextWrapped($"Other edited rows on this material: {string.Join(", ", strays.Select(RowName))}");
        ImUtf8.TextWrapped("These affect the gear too — leftovers from removed decals or older experiments."u8);
        if (ImUtf8.SmallButton("Clear These Rows"u8) && ImGui.GetIO().KeyCtrl)
        {
            foreach (var row in strays)
                edit.Rows.Remove(row);
            if (edit.IsEmpty)
                dTexture.Data.Materials.Remove(_selectedMaterial);
            Save(dTexture);
        }

        if (ImGui.IsItemHovered())
            ImUtf8.HoverTooltip("Hold Control and click to remove all listed row edits (the source values return)."u8);
    }

    private static string RowName(int row)
        => $"{row / 2 + 1}{(row % 2 == 0 ? 'A' : 'B')}";

    /// <summary> Eye icon that highlights a colorset row on the live model while hovered. </summary>
    private void DrawRowHighlightEye(TextureOption option, int row, ReadOnlySpan<byte> tooltip)
    {
        ImUtf8.IconButton(Dalamud.Interface.FontAwesomeIcon.Eye, tooltip);
        if (ImGui.IsItemHovered())
        {
            _highlightHovered = true;
            highlighter.Highlight(option.MaterialGamePath, option.Mtrl, row);
        }
    }

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

    private void DrawDecalLibrary(DTexture dTexture)
    {
        var canAdd = DefaultTargetOption() != null;
        using (ImRaii.Disabled(!canAdd))
        {
            if (ImUtf8.Button("Add Decal from Library..."u8))
            {
                // The picker outlives this frame; ignore the pick if the selection changed meanwhile.
                var owner = dTexture.Identifier;
                decalLibraryWindow.OpenAsPicker("Click a decal to stamp it onto the selected material — or import a new one.",
                    entry =>
                    {
                        if (_cacheOwner == owner)
                            AddLayer(dTexture, entry.Id, entry.Preset);
                    });
            }
        }

        ImUtf8.HoverTooltip(canAdd
            ? "Pick a decal from the library — its saved settings (colors, surface finish, size) are applied automatically."u8
            : "Select a material that supports decals first."u8);

        ImGui.SameLine();
        using (ImRaii.Disabled(!canAdd))
        {
            if (ImUtf8.Button("Import Decal..."u8))
                _fileDialog.OpenFileDialog("Import Decal", "Images{.png,.jpg,.jpeg,.dds,.bmp,.tga}", (success, path) =>
                {
                    if (!success)
                        return;

                    var entry = decals.Import(path);
                    if (entry != null && _cacheOwner == dTexture.Identifier)
                        AddLayer(dTexture, entry.Id, entry.Preset);
                });
        }

        ImUtf8.HoverTooltip("Import an image into the decal library and stamp it onto the selected material right away.\nTo import without stamping, use the Decal Library window (title-bar button)."u8);
    }

    private void AddLayer(DTexture dTexture, Guid decalId)
        => AddLayer(dTexture, decalId, decals.Get(decalId)?.Preset);

    private void AddLayer(DTexture dTexture, Guid decalId, DecalPreset? preset)
    {
        // The colorset id map is the preferred target: the decal is quantized and each of
        // its colors remaps texels to an automatically claimed colorset row. Materials
        // without one take color decals on their diffuse.
        var target = DefaultTargetOption();
        if (target == null)
            return;

        if (!dTexture.Data.Textures.TryGetValue(target.GamePath, out var layers))
        {
            layers                                   = [];
            dTexture.Data.Textures[target.GamePath] = layers;
        }

        CaptureTextureSource(dTexture, target.GamePath);

        var layer = new DecalLayer
        {
            DecalId   = decalId,
            IdRemap   = target.Slot is TextureSlot.Index,
            MaxColors = config.DefaultDecalMaxColors,
            Surface   = true,
        };
        // Body skin is one canvas but MANY connected parts (the genital strip and similar
        // meshes sit flush on the torso) — the clicked-part limit punches holes through
        // tattoos that cross them. The tight depth window still contains the projection.
        if (target.Kind is MaterialKind.Skin)
            layer.SurfaceLimitToPart = false;
        if (preset != null)
        {
            // The preset may opt out of colorset mode, but never forces it onto a diffuse target.
            layer.IdRemap        &= preset.IdRemap;
            layer.MaxColors       = preset.MaxColors;
            layer.AlphaThreshold  = preset.AlphaThreshold;
            layer.Opacity         = preset.Opacity;
            layer.ScaleX          = preset.ScaleX;
            layer.ScaleY          = preset.ScaleY;
            layer.RotationDeg     = preset.RotationDeg;
            layer.FlipX           = preset.FlipX;
            layer.FlipY           = preset.FlipY;
            layer.WorldWidth      = preset.WorldWidth;
            layer.WorldHeight     = preset.WorldHeight;
            layer.NormalSmooth    = preset.NormalSmooth;
            layer.Finish          = preset.Finish;
            layer.FinishRoughness = preset.FinishRoughness;
            layer.FinishSpecScale = preset.FinishSpecScale;
            layer.EffectScale     = preset.EffectScale;
        }

        layers.Add(layer);
        if (layer.IdRemap && target.Mtrl.Table is ColorTable table)
        {
            ReallocateDecal(dTexture, target, table, layer);

            // Restore the preset's saved recolors: quantization is deterministic for the same
            // image/settings, so the extracted palette lines up index-for-index. A count
            // mismatch means the image or settings changed since the preset was saved — the
            // fresh extraction wins then.
            if (preset != null && layer.PaletteRows.Count > 0 && preset.PaletteColors.Count == layer.PaletteRows.Count)
            {
                var edit = GetOrAddMaterialEdit(dTexture, target);
                for (var i = 0; i < layer.PaletteRows.Count; ++i)
                {
                    var color = new Rgba32(preset.PaletteColors[i]);
                    GetOrSeedRow(edit, table, layer.PaletteRows[i]).Diffuse = [color.R / 255f, color.G / 255f, color.B / 255f];
                    GetOrSeedRow(edit, table, layer.PaletteRows[i] + 1).Diffuse =
                        [color.R / 255f * ShadeFactor, color.G / 255f * ShadeFactor, color.B / 255f * ShadeFactor];
                }
            }
        }
        else if (preset is { PaletteColors.Count: > 0 })
        {
            // Diffuse target: the preset's saved recolors return as a composite-time tint.
            // Same determinism rule as colorset presets — a palette count mismatch means the
            // image or settings changed, and the decal's own pixels win.
            if (ExtractTintPalette(layer) && layer.PaletteColors.Count == preset.PaletteColors.Count)
            {
                layer.TintColors  = preset.PaletteColors.ToList();
                layer.TintEnabled = true;
            }
        }

        // 3D placement is the primary path: anchor at the texture's UV center and hand the
        // layer to the embedded viewport. Without mesh geometry the layer falls back to flat.
        var source = FindMaterialSource(dTexture);
        var mesh   = source == null ? null : uvReader.GetMesh(source);
        if (mesh == null)
        {
            layer.Surface = false;
        }
        else
        {
            SeedSurfaceFromUv(mesh, layer);
            BeginPlacement(dTexture, layer);
        }

        Save(dTexture);
    }

    /// <summary>
    /// Record the pristine source file of a texture the first time it gets a layer, so
    /// rebuilds always start from the original instead of our own already-baked output.
    /// </summary>
    private void CaptureTextureSource(DTexture dTexture, string gamePath)
        => overlayMods.GetOrCaptureTextureSource(dTexture, gamePath);

    /// <summary>
    /// Store the layer's current settings as the library entry's preset, so the next
    /// attachment of this decal — on any gear — starts from them. Colors are read back from
    /// the claimed rows, so manual recolors round-trip through the library.
    /// </summary>
    private void SaveLayerPreset(DTexture dTexture, TextureOption option, DecalLayer decal)
    {
        var preset = new DecalPreset
        {
            IdRemap         = decal.IdRemap,
            MaxColors       = decal.MaxColors,
            AlphaThreshold  = decal.AlphaThreshold,
            NormalSmooth    = decal.NormalSmooth,
            Finish          = decal.Finish,
            FinishRoughness = decal.FinishRoughness,
            FinishSpecScale = decal.FinishSpecScale,
            EffectScale     = decal.EffectScale,
            Opacity         = decal.Opacity,
            ScaleX          = decal.ScaleX,
            ScaleY          = decal.ScaleY,
            RotationDeg     = decal.RotationDeg,
            FlipX           = decal.FlipX,
            FlipY           = decal.FlipY,
            WorldWidth      = decal.WorldWidth,
            WorldHeight     = decal.WorldHeight,
        };

        if (decal.IdRemap && dTexture.Data.Materials.TryGetValue(option.MaterialGamePath, out var edit))
            for (var i = 0; i < decal.PaletteRows.Count; ++i)
                preset.PaletteColors.Add(edit.Rows.TryGetValue(decal.PaletteRows[i], out var rowEdit)
                    ? new Rgba32(rowEdit.Diffuse[0], rowEdit.Diffuse[1], rowEdit.Diffuse[2]).PackedValue
                    : i < decal.PaletteColors.Count
                        ? decal.PaletteColors[i]
                        : uint.MaxValue);
        else if (decal.HasTint)
            preset.PaletteColors.AddRange(decal.TintColors);

        decals.SetPreset(decal.DecalId, preset);
    }

    /// <summary> All decal layers of the selected material, across all of its textures. </summary>
    private void DrawLayers(DTexture dTexture)
    {
        var any = false;
        foreach (var option in MaterialOptions())
        {
            if (!dTexture.Data.Textures.TryGetValue(option.GamePath, out var layers) || layers.Count == 0)
                continue;

            any = true;
            DrawLayerList(dTexture, option, layers);
        }

        if (!any)
            ImUtf8.Text("No decals on this material yet — add one from the library above."u8);
    }

    private void DrawLayerList(DTexture dTexture, TextureOption option, List<TextureLayer> layers)
    {
        using var outerId = ImRaii.PushId(option.GamePath);

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
            var targetTag = option.Slot switch
            {
                TextureSlot.Index   => "  [colorset]",
                TextureSlot.Diffuse => string.Empty,
                _                   => $"  [{option.Slot}]",
            };
            var modeTag = decal.Surface
                ? decal is { AnchorX: 0f, AnchorY: 0f, AnchorZ: 0f } ? "  [3D — not placed!]" : "  [3D]"
                : string.Empty;
            var extractedTag = decal.Extracted ? "  [extracted]" : string.Empty;
            var errorTag     = decal.RowError != null ? "  [auto-disabled]" : string.Empty;
            if (!ImUtf8.CollapsingHeader($"{idx + 1}: {name}{targetTag}{extractedTag}{modeTag}{errorTag}###layer{idx}"))
                continue;

            using var indent = ImRaii.PushIndent();

            var changed = false;
            if (decal.IdRemap)
                changed |= DrawIdRemapSettings(dTexture, option, decal);
            else
                changed |= DrawTintSettings(decal);

            changed |= DrawMaterialEffects(dTexture, option, decal);
            changed |= DrawPlacementSettings(dTexture, decal);

            if (changed)
                Save(dTexture);

            if (ImUtf8.SmallButton("Remove"u8))
                remove = idx;
            ImGui.SameLine();
            if (ImUtf8.SmallButton("Up"u8) && idx > 0)
                swap = (idx, idx - 1);
            ImGui.SameLine();
            if (ImUtf8.SmallButton("Down"u8) && idx < layers.Count - 1)
                swap = (idx, idx + 1);
            ImGui.SameLine();
            if (ImUtf8.SmallButton("Save Settings to Library"u8))
                SaveLayerPreset(dTexture, option, decal);
            ImUtf8.HoverTooltip("Store this layer's colors, surface finish and size on the library entry.\nFuture attachments of this decal start from these settings — on any gear."u8);
        }

        if (remove >= 0)
        {
            var removedDecal = layers[remove] as DecalLayer;
            if (removedDecal != null)
            {
                CleanupSlotEdits(dTexture, removedDecal);
                if (_viewport.IsOpenFor(removedDecal))
                    _viewport.EndPlacement();
            }

            layers.RemoveAt(remove);
            // Removing an extraction returns the texture's source to the base mod (or
            // regenerates the cleaned copy from the remaining extractions).
            if (removedDecal is { Extracted: true, PreExtractionSource: not null })
                RestoreOrRegenerateSource(dTexture, option.GamePath, removedDecal);
            if (layers.Count == 0)
                dTexture.Data.Textures.Remove(option.GamePath);
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
    private bool DrawIdRemapSettings(DTexture dTexture, TextureOption option, DecalLayer decal)
    {
        if (option.Mtrl.Table is not ColorTable table)
            return false;

        EnsureIdStats(dTexture, option.GamePath);
        var changed = false;

        // Old saves and layers whose allocation was cleared claim their rows on first draw.
        // Saves from older schemes (a slot shared with another decal, or a slot's B half used
        // as its own color) fringe at decal edges — heal them by reallocating onto whole,
        // exclusively owned slots. Extracted layers own gear-authored rows and are never
        // re-quantized onto new ones.
        if (decal is { Extracted: false, Enabled: true, RowError: null })
        {
            var conflict = decal.PaletteRows.Count > 0
             && (decal.PaletteRows.Any(r => r % 2 == 1)
                 || (ClaimedRowsForMaterial(dTexture, option.MaterialGamePath, decal) is var otherRows
                     && decal.PaletteRows.Any(otherRows.Contains)));
            if (decal.PaletteRows.Count == 0 || conflict)
                changed |= ReallocateDecal(dTexture, option, table, decal);
        }

        if (decal.Extracted)
        {
            ImUtf8.TextWrapped("Extracted from this texture's id map — relocated onto its own claimed slots, seeded from the source rows."u8);
            ImUtf8.HoverTooltip(
                "This decal was lifted out of the id map and moved onto freshly claimed colorset slots that copy the source rows' authored look.\nRecoloring a slot recolors only the decal — the rest of the gear keeps its own rows."u8);
        }
        else
        {
            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            var maxColors = decal.MaxColors;
            if (ImUtf8.Slider("Max Colors"u8, ref maxColors, "%d"u8, 1, 12))
                decal.MaxColors = Math.Clamp(maxColors, 1, 12);
            if (ImGui.IsItemDeactivatedAfterEdit())
                changed |= ReallocateDecal(dTexture, option, table, decal);
            ImUtf8.HoverTooltip(
                "The decal is reduced to at most this many colors — similar colors merge.\nEach color claims one whole free colorset slot: its A row carries the color, its B row a darker shade the game blends toward where the gear baked its cloth shading.\nSo 6 colors need 6 fully free slots, and slots are never shared."u8);

            ImGui.SameLine();
            if (ImUtf8.SmallButton("Re-extract Colors"u8))
                changed |= ReallocateDecal(dTexture, option, table, decal);
            ImUtf8.HoverTooltip("Quantize the decal image again and reassign rows — discards manual recolors below."u8);
        }

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
            if (ImUtf8.ColorEdit($"Slot {row / 2 + 1}", ref color, ImGuiColorEditFlags.Float))
            {
                rowEdit.Diffuse = [color.X, color.Y, color.Z];
                // Keep the slot's B row a darkened copy so the baked shading blend darkens.
                GetOrSeedRow(edit, table, row + 1).Diffuse =
                    [color.X * ShadeFactor, color.Y * ShadeFactor, color.Z * ShadeFactor];
                changed = true;
            }

            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("This part of the decal renders in this color — recolor it without touching the image."u8);

            ImGui.SameLine();
            DrawRowHighlightEye(option, row,
                "Highlights the parts of the model this row colors while hovered (redraws your character).\nAfter a build, that includes the decal itself."u8);
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

            ApplyFinishToClaimedRows(edit, table, decal);
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

            ImUtf8.TextWrapped(lead.DyeTemplate > 0
                ? $"Dyes like the rest of this gear (template {lead.DyeTemplate})."
                : "No dye template detected on this gear — the decal will not react to dyes until a template id is set above.");
        }
        else
        {
            ImUtf8.Text("The decal keeps its colors when the gear is dyed."u8);
        }

        ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
        changed |= ImUtf8.Slider("Shape Threshold"u8, ref decal.AlphaThreshold, "%.2f"u8, 0.05f, 1f);
        if (!decal.Extracted && ImGui.IsItemDeactivatedAfterEdit())
            changed |= ReallocateDecal(dTexture, option, table, decal);
        ImUtf8.HoverTooltip(decal.Extracted
            ? "Decal pixels whose alpha is at or above this value become part of the stamped shape."u8
            : "Decal pixels whose alpha is at or above this value become part of the stamped shape.\nChanging it re-extracts the colors."u8);

        if (changed)
        {
            _slotPreviewDirty  = true;
            _slotPreviewMs     = Environment.TickCount64;
            _slotPreviewOption = option;
        }

        return changed;
    }

    /// <summary>
    /// The recolor editor for diffuse-target decals (skin tattoos, legacy gear): the decal is
    /// quantized to at most Max Colors and each extracted color gets an editable replacement,
    /// baked into the texture at composite time — the diffuse counterpart of the colorset
    /// slot editor. No material rows are involved, so recolors rebuild the textures instead.
    /// </summary>
    private bool DrawTintSettings(DecalLayer decal)
    {
        var changed = false;

        var tintEnabled = decal.TintEnabled;
        if (ImUtf8.Checkbox("Recolor Decal"u8, ref tintEnabled))
        {
            decal.TintEnabled = tintEnabled;
            if (tintEnabled && !decal.HasTint && ExtractTintPalette(decal))
                decal.TintColors = decal.PaletteColors.ToList();
            changed = true;
        }

        ImUtf8.HoverTooltip(
            "Extracts the decal's main colors and lets each be replaced — the recolors are baked into the texture on the next build.\nOff, the decal keeps its original image colors."u8);

        if (!decal.TintEnabled)
            return changed;

        ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
        var maxColors = decal.MaxColors;
        if (ImUtf8.Slider("Max Colors"u8, ref maxColors, "%d"u8, 1, 12))
            decal.MaxColors = Math.Clamp(maxColors, 1, 12);
        if (ImGui.IsItemDeactivatedAfterEdit() && ExtractTintPalette(decal))
        {
            decal.TintColors = decal.PaletteColors.ToList();
            changed          = true;
        }

        ImUtf8.HoverTooltip(
            "The decal is reduced to at most this many colors — similar colors merge.\nChanging it re-extracts the colors and discards the recolors below."u8);

        ImGui.SameLine();
        if (ImUtf8.SmallButton("Re-extract Colors"u8) && ExtractTintPalette(decal))
        {
            decal.TintColors = decal.PaletteColors.ToList();
            changed          = true;
        }

        ImUtf8.HoverTooltip("Quantize the decal image again — discards the recolors below."u8);

        if (decal.PaletteColors.Count == 0)
        {
            ImUtf8.TextWrapped("Could not extract any colors from the decal image — is the extraction threshold too high?"u8);
            return changed;
        }

        // One editable color per extracted color; the extracted swatch stays as reference so
        // recoloring never loses which image color it replaces.
        for (var i = 0; i < decal.PaletteColors.Count && i < decal.TintColors.Count; ++i)
        {
            using var id     = ImUtf8.PushId(i);
            var       source = new Rgba32(decal.PaletteColors[i]);

            ImGui.ColorButton("##extracted", new Vector4(source.R / 255f, source.G / 255f, source.B / 255f, 1f));
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("The color extracted from the decal image — image pixels closest to it render in the replacement color."u8);

            ImGui.SameLine();
            var tint  = new Rgba32(decal.TintColors[i]);
            var color = new Vector3(tint.R / 255f, tint.G / 255f, tint.B / 255f);
            ImGui.SetNextItemWidth(250 * ImUtf8.GlobalScale);
            if (ImUtf8.ColorEdit($"Color {i + 1}", ref color, ImGuiColorEditFlags.Float))
                decal.TintColors[i] = new Rgba32(color.X, color.Y, color.Z).PackedValue;
            // A save rebuilds the mod's textures — commit once the edit ends, not per drag frame.
            if (ImGui.IsItemDeactivatedAfterEdit())
                changed = true;

            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("This part of the decal renders in this color — recolor it without touching the image."u8);
        }

        ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
        ImUtf8.Slider("Extraction Threshold"u8, ref decal.AlphaThreshold, "%.2f"u8, 0.05f, 1f);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            if (ExtractTintPalette(decal))
                decal.TintColors = decal.PaletteColors.ToList();
            changed = true;
        }

        ImUtf8.HoverTooltip(
            "Decal pixels whose alpha is at or above this value feed the color extraction.\nBlending keeps the image's soft edges either way — this only affects which pixels count as colors."u8);

        return changed;
    }

    /// <summary> Quantize the decal image into the palette a tint maps against. Returns false when nothing usable was extracted. </summary>
    private bool ExtractTintPalette(DecalLayer decal)
    {
        var path = decals.FilePath(decal.DecalId);
        if (!File.Exists(path))
            return false;

        try
        {
            decal.PaletteColors = DecalQuantizer.ExtractPalette(path, decal.MaxColors, decal.AlphaThreshold).ToList();
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Failed to quantize decal {decal.DecalId}: {ex}");
            decal.PaletteColors = [];
        }

        return decal.PaletteColors.Count > 0;
    }

    /// <summary>
    /// Quantize the decal image and claim one free colorset row per extracted color. Rows
    /// the gear renders or other decals claim stay untouched; when not enough rows are free
    /// the layer is disabled with an error until rows free up or Max Colors shrinks.
    /// </summary>
    private bool ReallocateDecal(DTexture dTexture, TextureOption option, ColorTable table, DecalLayer decal)
    {
        // Extracted layers render through the gear's own authored rows — never reallocate.
        if (decal.Extracted)
            return false;

        var path = decals.FilePath(decal.DecalId);
        if (!File.Exists(path))
            return false;

        EnsureIdStats(dTexture, option.GamePath);
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
            var result = ColorRowAllocator.Allocate(palette.Length, EffectiveGearUsedPairs(dTexture, option.MaterialGamePath), others);
            decal.RowError = result.Error;
            if (result.Success)
            {
                decal.PaletteRows = result.Rows;
                for (var i = 0; i < result.Rows.Count; ++i)
                {
                    // The slot's A row carries the color; its B row gets a darkened copy —
                    // the id map's G channel blends A toward B exactly where the garment
                    // baked its cloth shading, so the shading stays visible on the decal.
                    var color = new Rgba32(palette[i]);
                    edit.Rows.Remove(result.Rows[i]);
                    edit.Rows.Remove(result.Rows[i] + 1);
                    GetOrSeedRow(edit, table, result.Rows[i]).Diffuse = [color.R / 255f, color.G / 255f, color.B / 255f];
                    GetOrSeedRow(edit, table, result.Rows[i] + 1).Diffuse =
                        [color.R / 255f * ShadeFactor, color.G / 255f * ShadeFactor, color.B / 255f * ShadeFactor];
                }

                // Freshly seeded rows carry the template's finish; re-apply the layer's own.
                ApplyFinishToClaimedRows(edit, table, decal);
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
    /// The scanner's gear-used slots minus the user's usable overrides — what row allocation
    /// actually blocks. The scanner marks a slot used over a single referencing texel, so
    /// the override exists for maps where stray pixels lock out effectively free slots.
    /// </summary>
    private IReadOnlySet<int> EffectiveGearUsedPairs(DTexture dTexture, string materialGamePath)
    {
        if (!dTexture.Data.Materials.TryGetValue(materialGamePath, out var edit) || edit.UsableSlots.Count == 0)
            return _usedRowPairs;

        var ret = new HashSet<int>(_usedRowPairs);
        ret.ExceptWith(edit.UsableSlots);
        return ret;
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
            var opt = OptionFor(gamePath);
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
    /// set a surface finish inside its footprint. The finish goes into the mask map and —
    /// for colorset decals — into the claimed rows' roughness/specular, which dominates
    /// perceived shine on colorset-driven gear. Off by default.
    /// </summary>
    private bool DrawMaterialEffects(DTexture dTexture, TextureOption option, DecalLayer decal)
    {
        var hasNormal = _options!.Any(o => string.Equals(o.MaterialGamePath, option.MaterialGamePath, StringComparison.OrdinalIgnoreCase)
         && o.Slot is TextureSlot.Normal);
        var hasMask = _options!.Any(o => string.Equals(o.MaterialGamePath, option.MaterialGamePath, StringComparison.OrdinalIgnoreCase)
         && o.Slot is TextureSlot.Mask);
        // Colorset decals carry their finish on the claimed rows, so the control works even
        // without a mask sibling. Mask finish semantics are authored for modern gear masks —
        // skin and legacy mask/specular maps encode different channels and stay untouched.
        var showFinish = decal.IdRemap || (hasMask && option.Kind is MaterialKind.ModernColorset);
        if (!hasNormal && !showFinish)
            return false;

        var changed       = false;
        var finishChanged = false;
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

        if (showFinish)
        {
            ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
            using (var combo = ImUtf8.Combo("Surface Finish"u8, FinishLabel(decal.Finish)))
            {
                if (combo)
                    foreach (var mode in Enum.GetValues<DecalFinishMode>())
                    {
                        if (!ImUtf8.Selectable(FinishLabel(mode), mode == decal.Finish) || mode == decal.Finish)
                            continue;

                        decal.Finish  = mode;
                        finishChanged = true;
                    }
            }

            ImUtf8.HoverTooltip(decal.IdRemap
                ? "How the surface responds to light under the decal — written into the claimed colorset rows (and the mask map, if the material has one).\nMatte suits cloth prints, Glossy suits stickers/vinyl; Custom exposes the raw values."u8
                : "How the surface responds to light under the decal, written into the material's mask map.\nMatte suits cloth prints, Glossy suits stickers/vinyl; Custom exposes the raw values.\nNote: on colorset-driven gear the underlying rows bound what the mask alone can change."u8);

            if (decal.Finish == DecalFinishMode.Custom)
            {
                ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
                var roughness = decal.FinishRoughness;
                if (ImUtf8.Slider("Roughness"u8, ref roughness, "%.2f"u8, 0f, 1f))
                {
                    decal.FinishRoughness = Math.Clamp(roughness, 0f, 1f);
                    finishChanged         = true;
                }

                ImUtf8.HoverTooltip("0 = mirror-glossy, 1 = fully matte."u8);

                ImGui.SetNextItemWidth(220 * ImUtf8.GlobalScale);
                var specScale = decal.FinishSpecScale;
                if (ImUtf8.Slider("Specular Scale"u8, ref specScale, "%.2f"u8, 0f, 2f))
                {
                    decal.FinishSpecScale = Math.Clamp(specScale, 0f, 2f);
                    finishChanged         = true;
                }

                ImUtf8.HoverTooltip("Multiplier on the authored specular color — below 1 dims reflections, above 1 boosts them."u8);
            }
            else if (decal.Finish != DecalFinishMode.Keep)
            {
                var (roughness, specScale) = FinishMapping.PresetValues(decal.Finish);
                using var disabled = ImRaii.Disabled();
                ImUtf8.Text($"Roughness {roughness:F2}, specular ×{specScale:F2}");
            }
        }

        if (decal.HasMaterialEffects && (hasNormal || hasMask))
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

        if (finishChanged)
        {
            changed = true;
            if (decal.IdRemap && option.Mtrl.Table is ColorTable table)
            {
                ApplyFinishToClaimedRows(GetOrAddMaterialEdit(dTexture, option), table, decal);
                _slotPreviewDirty  = true;
                _slotPreviewMs     = Environment.TickCount64;
                _slotPreviewOption = option;
            }
        }

        return changed;
    }

    private static string FinishLabel(DecalFinishMode mode)
        => mode switch
        {
            DecalFinishMode.Matte  => "Matte",
            DecalFinishMode.Glossy => "Glossy",
            DecalFinishMode.Custom => "Custom",
            _                      => "Keep",
        };

    /// <summary>
    /// Write the decal's finish into every claimed row (both halves of each pair). Rows are
    /// rebased onto a full template row first (keeping only colors and dye settings), so
    /// switching finishes or returning to Keep is idempotent. With an explicit finish the
    /// template must be a DIELECTRIC authored row: metal rows carry BRDF scalars that turn
    /// the diffuse path off, which rendered a white decal as dark grey once the finish
    /// cleared their Metalness.
    /// </summary>
    private void ApplyFinishToClaimedRows(MaterialEdit edit, ColorTable table, DecalLayer decal)
    {
        foreach (var row in decal.PaletteRows.SelectMany(r => new[] { r, r ^ 1 }).Distinct())
        {
            if (!edit.Rows.TryGetValue(row, out var rowEdit))
                continue;

            // Extracted layers render through the gear's own authored look — leave it alone
            // for Keep, and only stamp the absolute finish values on top otherwise.
            if (decal.Extracted)
            {
                if (decal.Finish != DecalFinishMode.Keep)
                    FinishMapping.ApplyToRow(rowEdit, decal);
                continue;
            }

            var templateIdx = SeedTemplateIndex(table, row);
            if (decal.Finish != DecalFinishMode.Keep && (float)table[templateIdx].Metalness >= 0.5f)
                templateIdx = DielectricTemplateIndex(table) ?? templateIdx;

            var seeded = ColorRowEdit.FromRow(row, table[templateIdx]);
            seeded.RowIndex     = row;
            seeded.Diffuse      = rowEdit.Diffuse;
            seeded.DyeMode      = rowEdit.DyeMode;
            seeded.DyeTemplate  = rowEdit.DyeTemplate;
            seeded.DyeChannel   = rowEdit.DyeChannel;
            seeded.DyeDiffuse   = rowEdit.DyeDiffuse;
            seeded.DyeSpecular  = rowEdit.DyeSpecular;
            seeded.DyeEmissive  = rowEdit.DyeEmissive;
            seeded.DyeRoughness = rowEdit.DyeRoughness;
            seeded.DyeMetalness = rowEdit.DyeMetalness;
            seeded.DyeSheen     = rowEdit.DyeSheen;
            edit.Rows[row]      = seeded;

            if (decal.Finish != DecalFinishMode.Keep)
                FinishMapping.ApplyToRow(seeded, decal);
        }
    }

    /// <summary>
    /// The most-rendered authored non-metal row — the template whose BRDF scalars suit a
    /// dielectric print. Null when the gear authors no dielectric rows at all.
    /// </summary>
    private int? DielectricTemplateIndex(ColorTable table)
    {
        foreach (var (idx, _) in _rowUsageCounts.OrderByDescending(kvp => kvp.Value))
            if (idx >= 0 && idx < ColorTable.NumRows && !IsFillerRow(table[idx]) && (float)table[idx].Metalness < 0.5f)
                return idx;

        for (var i = 0; i < ColorTable.NumRows; ++i)
            if (!IsFillerRow(table[i]) && (float)table[i].Metalness < 0.5f)
                return i;

        return null;
    }

    private MaterialEdit GetOrAddMaterialEdit(DTexture dTexture, TextureOption option)
    {
        if (dTexture.Data.Materials.TryGetValue(option.MaterialGamePath, out var edit))
            return edit;

        edit = new MaterialEdit { ShaderName = option.Mtrl.ShaderPackage.Name };
        dTexture.Data.Materials[option.MaterialGamePath] = edit;
        return edit;
    }

    /// <param name="templateRow">
    /// The source row the seed copies its values from; defaults to the safe authored row
    /// <see cref="SeedTemplateIndex"/> picks. Extraction passes the lifted decal's own
    /// source row so the relocated slot keeps its authored look.
    /// </param>
    private ColorRowEdit GetOrSeedRow(MaterialEdit edit, ColorTable table, int rowIndex, int? templateRow = null)
    {
        if (edit.Rows.TryGetValue(rowIndex, out var row))
            return row;

        var seeded = ColorRowEdit.FromRow(rowIndex, table[templateRow ?? SeedTemplateIndex(table, rowIndex)]);
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

        if (!dTexture.Data.Materials.TryGetValue(_selectedMaterial, out var edit))
            return;

        var others = ClaimedRowsForMaterial(dTexture, _selectedMaterial, removed);
        foreach (var row in removed.PaletteRows.SelectMany(r => new[] { r, r ^ 1 }).Distinct().Where(r => !others.Contains(r)))
            edit.Rows.Remove(row);
        if (edit.IsEmpty)
            dTexture.Data.Materials.Remove(_selectedMaterial);
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

    private bool _mdlHealAttempted;

    /// <summary>
    /// The source entry of the selected material. Sources saved before model paths were
    /// captured are healed once per selection through a live resource-tree resolve.
    /// </summary>
    private SourcePath? FindMaterialSource(DTexture dTexture)
    {
        var source = dTexture.Data.Source.Materials.FirstOrDefault(m
            => string.Equals(m.GamePath, _selectedMaterial, StringComparison.OrdinalIgnoreCase));
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

    #region Surface placement

    private string _placementError = string.Empty;

    /// <summary> Placement controls of one decal layer: 3D surface projection or flat UV stamping. </summary>
    private bool DrawPlacementSettings(DTexture dTexture, DecalLayer decal)
    {
        var changed = false;
        var surface = decal.Surface;
        if (ImUtf8.Checkbox("Place on Model (3D)"u8, ref surface))
        {
            decal.Surface = surface;
            changed       = true;
            // Entering 3D mode keeps the decal where it is: the current UV position is
            // converted to a mesh anchor. Only when that fails does placement mode open.
            if (surface && decal is { AnchorX: 0f, AnchorY: 0f, AnchorZ: 0f })
            {
                var source = FindMaterialSource(dTexture);
                var mesh   = source == null ? null : uvReader.GetMesh(source);
                if (mesh == null || !SeedSurfaceFromUv(mesh, decal))
                    BeginPlacement(dTexture, decal);
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

            if (ImUtf8.Button(_viewport.IsOpenFor(decal) ? "Stop Placing"u8 : "Place in 3D View"u8))
            {
                if (_viewport.IsOpenFor(decal))
                    _viewport.EndPlacement();
                else
                    BeginPlacement(dTexture, decal);
            }

            ImUtf8.HoverTooltip(
                "Bind this decal to the 3D preview below — stamp and drag it directly on the mesh,\norbit and zoom freely, and changes apply to the mod when you finish an adjustment."u8);

            if (_placementError.Length > 0)
                using (ImRaii.PushColor(ImGuiCol.Text, 0xFF00A0FFu))
                    ImUtf8.TextWrapped(_placementError);
            else if (decal is { AnchorX: 0f, AnchorY: 0f, AnchorZ: 0f })
                using (ImRaii.PushColor(ImGuiCol.Text, 0xFF00A0FFu))
                    ImUtf8.TextWrapped("NOT PLACED YET — the decal stays invisible until you place it in the 3D view below."u8);
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

            if (ImUtf8.SmallButton("Flip H"u8))
            {
                decal.FlipX = !decal.FlipX;
                changed     = true;
            }

            ImUtf8.HoverTooltip("Mirror the decal horizontally."u8);
            ImGui.SameLine();
            if (ImUtf8.SmallButton("Flip V"u8))
            {
                decal.FlipY = !decal.FlipY;
                changed     = true;
            }

            ImUtf8.HoverTooltip("Mirror the decal vertically."u8);

            ImUtf8.TextWrapped("Flat UV placement — check the result in the 3D preview below or the Textures tab, or switch to Place on Model (3D)."u8);
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
            // Context triangles map into a different texture — their UVs mean nothing here.
            if (!mesh.TriangleEditable[i / 3])
                continue;

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

            // A quarter of a full-BODY texture seeds a poster-sized projector whose depth
            // window (0.4 × size) catches arms and both thighs in one stamp. Seed body
            // tattoos at a sane size instead — the size sliders still go up from there.
            if (ModelUvReader.IsBodySkinMaterial(mesh.GamePath))
            {
                var maxDim = MathF.Max(decal.WorldWidth, decal.WorldHeight);
                if (maxDim > 0.15f)
                {
                    var scale = 0.15f / maxDim;
                    decal.WorldWidth  *= scale;
                    decal.WorldHeight *= scale;
                }
            }
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

    /// <summary> Bind a decal layer to the embedded viewport for interactive placement. </summary>
    private void BeginPlacement(DTexture dTexture, DecalLayer decal)
    {
        var source = FindMaterialSource(dTexture);

        // The worn model can differ from the one captured at selection time (e.g. another
        // size option) — re-resolve so the viewport shows the variant actually in use.
        // Body skin meshes resolve their own SmallClothes model set at load time; healing
        // their recorded model here would only poison the single-model fallback.
        if (source is { MdlGamePath.Length: > 0 } && penumbra.Available && !ModelUvReader.IsBodySkinMaterial(source.GamePath))
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
            _placementError = source != null && ModelUvReader.IsBodySkinMaterial(source.GamePath)
                ? "Your current body does not use this skin material — run Load Skin in the Source tab again and add the body material listed there."
                : "No mesh geometry available — re-add the material in the Source tab while wearing the gear.";
            return;
        }

        _placementError = string.Empty;
        _viewport.Open(dTexture, mesh, modelState.CurrentAttributeMask(mesh.GamePath));
        _viewport.BeginPlacement(decal, decals.FilePath(decal.DecalId), () => Save(dTexture));
    }

    #endregion

    #region 3D preview shading

    private readonly record struct ShadingKey(int DiffuseVersion, int IndexVersion, int RowVersion, bool Placement, uint SkinTone, int OverlayVersionHash);

    private Vector3[]?  _rowDiffuse;
    private int         _rowDiffuseVersion;
    private string      _rowDiffuseMaterial = string.Empty;
    private ShadingKey? _shadingKey;

    private void ResetShadingState()
    {
        _rowDiffuse         = null;
        _rowDiffuseMaterial = string.Empty;
        _shadingKey         = null;
    }

    /// <summary> The embedded 3D preview of the selected material, textured and colorset-aware. </summary>
    private void DrawViewport(DTexture dTexture)
    {
        var source = FindMaterialSource(dTexture);
        var mesh   = source == null ? null : uvReader.GetMesh(source);
        if (mesh == null)
        {
            ImUtf8.TextWrapped(source != null && ModelUvReader.IsBodySkinMaterial(source.GamePath)
                ? "Your current body does not use this skin material — run Load Skin in the Source tab again and add the body material listed there."u8
                : "No mesh geometry available for a 3D preview — re-add the material in the Source tab while wearing the gear."u8);
            return;
        }

        // A bound placement layer must belong to the selected material — switching materials
        // would otherwise pair its overlay with the wrong mesh and shading.
        var placement = _viewport.PlacementLayer;
        if (placement != null)
        {
            var ownerOption = LayerOwnerPath(dTexture, placement) is { } path ? OptionFor(path) : null;
            if (ownerOption == null || !string.Equals(ownerOption.MaterialGamePath, _selectedMaterial, StringComparison.OrdinalIgnoreCase))
                _viewport.EndPlacement();
        }

        _viewport.Open(dTexture, mesh, modelState.CurrentAttributeMask(mesh.GamePath));
        UpdateViewportShading(dTexture);
        _viewport.Draw(dTexture);
    }

    /// <summary>
    /// Assemble the viewport's shading inputs: the composited diffuse and id map of the
    /// selected material plus the resolved colorset row colors. While a decal is being
    /// placed, its own texture uses a base composited WITHOUT that layer (an exclude-layer
    /// entry of the shared preview cache) — otherwise the already-baked copy would ghost at
    /// the old position while dragging.
    /// </summary>
    private void UpdateViewportShading(DTexture dTexture)
    {
        var diffuseOption = DiffuseOption();
        var indexOption   = IndexOption();

        if (!string.Equals(_rowDiffuseMaterial, _selectedMaterial, StringComparison.OrdinalIgnoreCase))
        {
            var mtrl = (indexOption ?? diffuseOption ?? MaterialOptions().FirstOrDefault())?.Mtrl;
            _rowDiffuse = mtrl == null
                ? null
                : MaterialEditApplier.ResolveRowDiffuse(mtrl, dTexture.Data.Materials.GetValueOrDefault(_selectedMaterial));
            _rowDiffuseMaterial = _selectedMaterial;
            ++_rowDiffuseVersion;
        }

        var placementLayer = _viewport.PlacementLayer;
        var boundPath      = placementLayer == null ? null : LayerOwnerPath(dTexture, placementLayer);

        CompositePreviewCache.Entry? EntryFor(TextureOption? option)
            => option == null
                ? null
                : previewCache.Get(dTexture, option.GamePath,
                    string.Equals(boundPath, option.GamePath, StringComparison.OrdinalIgnoreCase) ? placementLayer : null);

        var diffuseEntry = EntryFor(diffuseOption);
        var indexEntry   = EntryFor(indexOption);

        // Skin diffuse textures are pale neutral maps the game tints with the customize skin
        // color — stand in with the configured preview tone so the preview resembles skin.
        var skinTone = SelectedKind() is MaterialKind.Skin ? config.PreviewSkinTone : 0u;

        // Overlay-part meshes (nails, accents) rendered alongside the body, each with its own
        // composited texture from the SAME preview cache the companion bake writes through —
        // so the live preview matches the built result, including mid-drag. Only relevant when
        // the body itself is the selected/primary mesh (overlays share its model set).
        var overlayEntries = BuildOverlayEntries(dTexture, placementLayer, boundPath, out var overlayVersionHash);

        var key = new ShadingKey(diffuseEntry?.Version ?? -1, indexEntry?.Version ?? -1, _rowDiffuseVersion,
            placementLayer != null, skinTone, overlayVersionHash);
        if (key == _shadingKey)
            return;

        _shadingKey = key;

        static DecodedTexture? Buffer(CompositePreviewCache.Entry? entry)
            => entry?.Pristine == null
                ? null
                : entry.Composited != null
                    ? new DecodedTexture(entry.Composited, entry.Pristine.Width, entry.Pristine.Height)
                    : entry.Pristine;

        Vector3? tone = null;
        if (skinTone != 0u)
        {
            var packed = new Rgba32(skinTone);
            tone = new Vector3(packed.R / 255f, packed.G / 255f, packed.B / 255f);
        }

        _viewport.UpdateShading(new ViewportShading(Buffer(diffuseEntry), Buffer(indexEntry), _rowDiffuse, tone));
        _viewport.SetOverlays(overlayEntries);
    }

    /// <summary>
    /// Overlay-part viewport entries (nails, accents): each gets its own mesh (routes through
    /// <see cref="ModelUvReader.GetBodyMesh"/> exactly like Part B's companion bake, so the
    /// editable geometry matches) and its own composited diffuse from the shared preview cache.
    /// Empty unless the selected/primary material is the body itself — overlays share its
    /// SmallClothes model set and would be meaningless alongside an unrelated gear mesh.
    /// </summary>
    private List<ViewportOverlay> BuildOverlayEntries(DTexture dTexture, DecalLayer? placementLayer, string? boundPath,
        out int versionHash)
    {
        versionHash = 0;
        var result = new List<ViewportOverlay>();
        if (_overlayOptions is not { Count: > 0 } || !ModelUvReader.IsBodySkinMaterial(_selectedMaterial))
            return result;

        foreach (var option in _overlayOptions)
        {
            var source = dTexture.Data.Source.Materials.FirstOrDefault(m
                => string.Equals(m.GamePath, option.MaterialGamePath, StringComparison.OrdinalIgnoreCase));
            var mesh = source == null ? null : uvReader.GetMesh(source);
            if (mesh == null)
                continue;

            var entry = previewCache.Get(dTexture, option.GamePath,
                string.Equals(boundPath, option.GamePath, StringComparison.OrdinalIgnoreCase) ? placementLayer : null);
            versionHash = HashCode.Combine(versionHash, entry.Version);

            var diffuse = entry.Pristine == null
                ? null
                : entry.Composited != null
                    ? new DecodedTexture(entry.Composited, entry.Pristine.Width, entry.Pristine.Height)
                    : entry.Pristine;
            result.Add(new ViewportOverlay(mesh, diffuse, option.Kind is MaterialKind.Skin));
        }

        return result;
    }

    #endregion

    #region Colorset decal extraction

    private readonly HashSet<int> _extractRows = [];
    private bool                  _extractLargestOnly;
    private string                _extractStatus = string.Empty;

    /// <summary>
    /// Colorset management for the material's id map: which slots the map references, a
    /// per-slot override to hand "used" slots back to the decal allocator (the scanner
    /// blocks a slot over a single stray texel), and extraction of baked decals. Extraction
    /// selects per ROW (a slot's A and B halves separately) because a baked decal often
    /// shares its slot with the garment — e.g. the decal on 3B while 3A colors the cloth —
    /// and relocates the content onto freshly claimed slots of its own.
    /// </summary>
    private void DrawExtractionSection(DTexture dTexture)
    {
        var option = IndexOption();
        if (option == null || option.Mtrl.Table is not ColorTable table)
            return;

        ImGui.Separator();
        if (!ImUtf8.CollapsingHeader("Manage Colorset"u8))
            return;

        using var indent = ImRaii.PushIndent();

        // Which file the analysis actually reads — a stale capture (taken before the source
        // mod was enabled or updated) is the usual reason a baked decal does not show up.
        var capturedPath = dTexture.Data.TextureSourcePaths.GetValueOrDefault(option.GamePath);
        var cleanedFile  = filenames.ExtractedSourceFile(dTexture.Identifier, option.GamePath);
        var sourceLabel = capturedPath switch
        {
            null => "unresolved — falling back to the vanilla game file",
            ""   => "vanilla game file",
            _ when string.Equals(capturedPath, cleanedFile, StringComparison.OrdinalIgnoreCase)
                 => "cleaned copy (extracted decals removed)",
            _ => Path.GetFileName(capturedPath),
        };
        ImUtf8.TextWrapped($"Analyzing id map: {sourceLabel}");
        if (capturedPath is { Length: > 0 } && ImGui.IsItemHovered())
            ImUtf8.HoverTooltip(capturedPath);
        ImGui.SameLine();
        if (ImUtf8.SmallButton("Reload Source"u8))
        {
            dTexture.Data.TextureSourcePaths.Remove(option.GamePath);
            _statsTexture = string.Empty;
            previewCache.Invalidate(dTexture.Identifier, option.GamePath);
            // With extractions present, rebase them onto the fresh capture and rebuild the
            // cleaned copy — otherwise the redirect to it would just be dropped.
            var fresh = overlayMods.GetOrCaptureTextureSource(dTexture, option.GamePath);
            var extracted = dTexture.Data.Textures.GetValueOrDefault(option.GamePath)?.OfType<DecalLayer>()
                    .Where(l => l.Extracted).ToList()
             ?? [];
            if (extracted.Count > 0)
            {
                foreach (var l in extracted)
                    l.PreExtractionSource = fresh ?? string.Empty;
                RegenerateCleanedSource(dTexture, option.GamePath);
            }

            saveService.QueueSave(dTexture);
        }

        ImUtf8.HoverTooltip(
            "Drop the stored source capture and resolve the id map again from the currently active mods.\nUse this when the analyzed file is not the one your mod actually ships (e.g. the capture predates enabling or updating the source mod)."u8);

        EnsureIdStats(dTexture, option.GamePath);
        if (_sortedRowUsage.Count == 0)
        {
            ImUtf8.Text("No id-map statistics available for this texture."u8);
            return;
        }

        var claimedRows = ClaimedRowsForMaterial(dTexture, option.MaterialGamePath, null);
        var rowDiffuse  = MaterialEditApplier.ResolveRowDiffuse(option.Mtrl, null);

        DrawSlotAvailability(dTexture, option, claimedRows, rowDiffuse);

        ImGui.Separator();
        ImUtf8.Text("Extract Baked Decal"u8);
        ImUtf8.HoverTooltip(
            "Lift a decal that is already baked into this id map (e.g. by the source mod) out into a decal layer of its own: hover the eye of each row to see where it renders on your character, pick the row(s) the baked decal uses, then extract. The decal is moved onto free colorset slots of its own — the original texels are filled with the surrounding garment — so it can be recolored and repositioned without touching the rest of the gear."u8);

        _extractRows.RemoveWhere(claimedRows.Contains);

        // Small regions first — a baked decal is usually a small fraction of the garment.
        foreach (var (row, count) in _sortedRowUsage)
        {
            using var id      = ImUtf8.PushId(row);
            var       claimed = claimedRows.Contains(row);
            var       picked  = _extractRows.Contains(row);
            using (ImRaii.Disabled(claimed))
            {
                if (ImUtf8.Checkbox($"Row {RowName(row)}", ref picked))
                {
                    if (picked)
                        _extractRows.Add(row);
                    else
                        _extractRows.Remove(row);
                }
            }

            ImGui.SameLine();
            var color = rowDiffuse == null ? Vector3.One : rowDiffuse[row];
            ImGui.ColorButton("##rowColor",
                new Vector4(Math.Clamp(color.X, 0f, 1f), Math.Clamp(color.Y, 0f, 1f), Math.Clamp(color.Z, 0f, 1f), 1f));
            ImGui.SameLine();
            ImUtf8.Text($"{count} texels ({100f * count / _statsTotalTexels:F1}%){(claimed ? "  — claimed by a decal layer" : string.Empty)}");
            ImGui.SameLine();
            DrawRowHighlightEye(option, row,
                "Highlights where this row dominantly renders on the character while hovered (redraws your character).\nA baked decal usually lives on a row the garment itself barely uses — often a slot's B half."u8);
        }

        ImUtf8.Checkbox("Largest Connected Region Only"u8, ref _extractLargestOnly);
        ImUtf8.HoverTooltip(
            "Keep only the biggest connected patch of the selected rows.\nUseful when a row also covers unrelated texels elsewhere (a B half additionally catches the garment's deepest baked shading) — but turn it OFF if the decal itself is smaller than those other patches."u8);

        if (ImUtf8.Button("Extract Selected Rows as Decal"u8) && _extractRows.Count > 0)
            ExtractDecal(dTexture, option, table);
        if (_extractRows.Count == 0)
            ImUtf8.HoverTooltip("Select at least one row above first."u8);

        if (_extractStatus.Length > 0)
            ImUtf8.TextWrapped(_extractStatus);
    }

    /// <summary>
    /// The material's 16 colorset slots with their allocation status: free, claimed by a
    /// decal, or referenced by the id map — the latter with a per-slot override handing the
    /// slot back to the allocator. The scanner blocks a slot over a single referencing
    /// texel, so stray pixels in modded maps can lock out slots that are effectively free;
    /// the texel counts are the judgment call.
    /// </summary>
    private void DrawSlotAvailability(DTexture dTexture, TextureOption option, HashSet<int> claimedRows, Vector3[]? rowDiffuse)
    {
        ImUtf8.Text("Slot Availability"u8);
        ImUtf8.HoverTooltip(
            "Which colorset slots decals may claim. Free slots are used automatically; slots the id map references are blocked — but a slot referenced by only a handful of stray texels is often fine to hand over with the Usable checkbox."u8);

        var edit = dTexture.Data.Materials.GetValueOrDefault(option.MaterialGamePath);
        for (var pair = 1; pair <= ColorRowAllocator.PairCount; ++pair)
        {
            using var id   = ImUtf8.PushId(pair);
            var       rowA = (pair - 1) * 2;

            var color = rowDiffuse == null ? Vector3.One : rowDiffuse[rowA];
            ImGui.ColorButton("##slotColor",
                new Vector4(Math.Clamp(color.X, 0f, 1f), Math.Clamp(color.Y, 0f, 1f), Math.Clamp(color.Z, 0f, 1f), 1f));
            ImGui.SameLine();
            ImUtf8.Text($"Slot {pair,2}");
            ImGui.SameLine();

            if (claimedRows.Contains(rowA) || claimedRows.Contains(rowA + 1))
            {
                ImUtf8.Text("— claimed by a decal"u8);
                continue;
            }

            if (!_usedRowPairs.Contains(pair))
            {
                ImUtf8.Text("— free"u8);
                continue;
            }

            var usable = edit?.UsableSlots.Contains(pair) ?? false;
            if (ImUtf8.Checkbox("Usable"u8, ref usable))
            {
                var target = GetOrAddMaterialEdit(dTexture, option);
                if (usable)
                {
                    if (!target.UsableSlots.Contains(pair))
                        target.UsableSlots.Add(pair);
                }
                else
                {
                    target.UsableSlots.Remove(pair);
                    if (target.IsEmpty)
                        dTexture.Data.Materials.Remove(option.MaterialGamePath);
                }

                Save(dTexture);
            }

            ImUtf8.HoverTooltip(
                "Let decals claim this slot even though the id map references it.\nUse when the scanner is wrong (a few stray texels) or to sacrifice the slot deliberately — decals will overwrite its rows wherever the map really renders them."u8);

            ImGui.SameLine();
            var texels = _rowUsageCounts.GetValueOrDefault(rowA) + _rowUsageCounts.GetValueOrDefault(rowA + 1);
            ImUtf8.Text($"— used by the map, {texels} texels ({100f * texels / _statsTotalTexels:F1}%)");
        }
    }

    private void ExtractDecal(DTexture dTexture, TextureOption option, ColorTable table)
    {
        _extractStatus = string.Empty;

        // An empty capture means vanilla — TextureIO.Load falls back to game data for it.
        var diskPath   = overlayMods.GetOrCaptureTextureSource(dTexture, option.GamePath);
        var decoded    = textureIO.Load(option.GamePath, diskPath, null);
        var rowDiffuse = MaterialEditApplier.ResolveRowDiffuse(option.Mtrl, null);
        if (decoded == null || rowDiffuse == null)
        {
            _extractStatus = "Could not load the id map or its colorset.";
            return;
        }

        var extraction = ColorsetDecalExtractor.Extract(decoded, _extractRows, rowDiffuse, _extractLargestOnly);
        if (extraction == null)
        {
            _extractStatus = "The selected rows cover no texels — nothing to extract.";
            return;
        }

        // The extracted content moves onto freshly claimed slots: its source rows may be
        // shared with the garment (decal on 3B, cloth on 3A), so keeping them would couple
        // every recolor to the gear. One whole free pair per source row, like any decal.
        EnsureIdStats(dTexture, option.GamePath);
        var others     = ClaimedRowsForMaterial(dTexture, option.MaterialGamePath, null);
        var allocation = ColorRowAllocator.Allocate(extraction.Rows.Count,
            EffectiveGearUsedPairs(dTexture, option.MaterialGamePath), others);
        if (!allocation.Success)
        {
            _extractStatus = allocation.Error!;
            return;
        }

        DecalEntry? entry;
        using (var stamp = extraction.Stamp)
        {
            entry = decals.ImportGenerated(stamp, $"{option.MaterialLabel} — extracted decal");
        }

        if (entry == null)
        {
            _extractStatus = "Could not save the extracted stamp image.";
            return;
        }

        CaptureTextureSource(dTexture, option.GamePath);
        if (!dTexture.Data.Textures.TryGetValue(option.GamePath, out var layers))
        {
            layers                                   = [];
            dTexture.Data.Textures[option.GamePath] = layers;
        }

        var layer = new DecalLayer
        {
            DecalId             = entry.Id,
            IdRemap             = true,
            Extracted           = true,
            WriteBlendFromAlpha = true,
            PaletteColors       = extraction.RowColors.ToList(),
            PaletteRows         = allocation.Rows,
            MaxColors           = extraction.Rows.Count,
            FillPair            = extraction.FillPair,
            FillBlend           = extraction.FillBlend,
            SourceU             = (float)extraction.X / extraction.MapWidth,
            SourceV             = (float)extraction.Y / extraction.MapHeight,
            SourceUW            = (float)extraction.W / extraction.MapWidth,
            SourceUH            = (float)extraction.H / extraction.MapHeight,
            Surface             = false,
        };
        // Texel-exact original placement, so the restamp lands exactly on the erased region.
        layer.PosU   = layer.SourceU + layer.SourceUW / 2f;
        layer.PosV   = layer.SourceV + layer.SourceUH / 2f;
        layer.ScaleX = layer.SourceUW;
        layer.ScaleY = layer.SourceUH;

        // The texture's source becomes a cleaned copy with the decal removed; the original
        // is remembered so removing the extraction returns the source to the base mod. A
        // second extraction on the same texture shares the first one's true base.
        layer.PreExtractionSource = layers.OfType<DecalLayer>()
                .FirstOrDefault(l => l is { Extracted: true, PreExtractionSource: not null })?.PreExtractionSource
         ?? dTexture.Data.TextureSourcePaths.GetValueOrDefault(option.GamePath)
         ?? string.Empty;
        layers.Add(layer);
        RegenerateCleanedSource(dTexture, option.GamePath);

        // Seed each claimed slot from its SOURCE row so the decal keeps its authored look
        // (specular, roughness, tile — everything, not just the color); the slot's B half
        // becomes the standard darkened shade partner for benign edge blends.
        var edit = GetOrAddMaterialEdit(dTexture, option);
        for (var i = 0; i < allocation.Rows.Count; ++i)
        {
            var newRow = allocation.Rows[i];
            var srcRow = extraction.Rows[i];
            edit.Rows.Remove(newRow);
            edit.Rows.Remove(newRow + 1);

            var seededA = GetOrSeedRow(edit, table, newRow, srcRow);
            var seededB = GetOrSeedRow(edit, table, newRow + 1, srcRow);
            seededB.Diffuse = [seededA.Diffuse[0] * ShadeFactor, seededA.Diffuse[1] * ShadeFactor, seededA.Diffuse[2] * ShadeFactor];
        }

        _extractRows.Clear();
        _extractStatus =
            $"Extracted {extraction.Rows.Count} row(s) into decal \"{entry.Name}\" ({extraction.W}x{extraction.H} texels), "
          + $"relocated onto slot(s) {string.Join(", ", allocation.Rows.Select(r => r / 2 + 1))}. "
          + "The texture's source is now a cleaned copy with the decal removed — anything left behind shows in the row list above.";
        DynamicTextureManager.Log.Information(
            $"Extracted colorset decal from {option.GamePath}: rows [{string.Join(", ", extraction.Rows.Select(RowName))}] -> "
          + $"slots [{string.Join(", ", allocation.Rows.Select(r => r / 2 + 1))}], "
          + $"rect {extraction.X},{extraction.Y} {extraction.W}x{extraction.H}, fill pair {extraction.FillPair + 1} blend {extraction.FillBlend}.");
        Save(dTexture);
    }

    /// <summary>
    /// Rebuild the cleaned source copy of a texture: its true base (the source before any
    /// extraction) with every extracted decal's footprint erased, written next to the config
    /// and set as the texture's captured source. Builds and previews then start from a map
    /// that no longer contains the extracted decals.
    /// </summary>
    private void RegenerateCleanedSource(DTexture dTexture, string gamePath)
    {
        var extracted = dTexture.Data.Textures.GetValueOrDefault(gamePath)?.OfType<DecalLayer>()
                .Where(l => l is { Extracted: true, PreExtractionSource: not null }).ToList()
         ?? [];
        if (extracted.Count == 0)
            return;

        var basePath = extracted[0].PreExtractionSource!;
        var decoded  = textureIO.Load(gamePath, basePath, null);
        if (decoded == null)
        {
            DynamicTextureManager.Log.Warning($"Could not load the base source of {gamePath} to build its cleaned copy.");
            return;
        }

        using var image = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(decoded.Rgba, decoded.Width, decoded.Height);
        foreach (var layer in extracted)
            TextureCompositor.EraseExtractedFootprint(image, layer, decals.FilePath(layer.DecalId));

        var file = filenames.ExtractedSourceFile(dTexture.Identifier, gamePath);
        Directory.CreateDirectory(filenames.ExtractedDirectory);
        image.SaveAsPng(file);
        dTexture.Data.TextureSourcePaths[gamePath] = file;
        _statsTexture = string.Empty;
        previewCache.Invalidate(dTexture.Identifier, gamePath);
        DynamicTextureManager.Log.Information(
            $"Rebuilt cleaned source of {gamePath} from \"{(basePath.Length == 0 ? "vanilla" : basePath)}\" minus {extracted.Count} extracted decal(s).");
    }

    /// <summary>
    /// After removing an extracted layer: regenerate the cleaned copy from the remaining
    /// extractions, or — when it was the last one — restore the original source capture and
    /// delete the copy, returning the texture to the base mod.
    /// </summary>
    private void RestoreOrRegenerateSource(DTexture dTexture, string gamePath, DecalLayer removed)
    {
        var remaining = dTexture.Data.Textures.GetValueOrDefault(gamePath)?.OfType<DecalLayer>()
            .Any(l => l is { Extracted: true, PreExtractionSource: not null }) ?? false;
        if (remaining)
        {
            RegenerateCleanedSource(dTexture, gamePath);
            return;
        }

        dTexture.Data.TextureSourcePaths[gamePath] = removed.PreExtractionSource!;
        try
        {
            File.Delete(filenames.ExtractedSourceFile(dTexture.Identifier, gamePath));
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not delete the cleaned source copy of {gamePath}: {ex.Message}");
        }

        _statsTexture = string.Empty;
        previewCache.Invalidate(dTexture.Identifier, gamePath);
        DynamicTextureManager.Log.Information(
            $"Removed last extraction of {gamePath} — source restored to \"{(removed.PreExtractionSource!.Length == 0 ? "vanilla" : removed.PreExtractionSource)}\".");
    }

    #endregion

    /// <summary>
    /// Id-map usage statistics for a texture: which row pairs it references, how often each
    /// row actually renders (the G channel blends a pair's A row at 255 with its B row at 0)
    /// and how many texels each pair covers. Row seeding and decal extraction depend on
    /// these, so they are computed on demand.
    /// </summary>
    private void EnsureIdStats(DTexture dTexture, string gamePath)
    {
        if (_statsTexture == gamePath)
            return;

        var diskPath = overlayMods.GetOrCaptureTextureSource(dTexture, gamePath);
        var decoded  = textureIO.Load(gamePath, diskPath, null);
        if (decoded == null)
        {
            // Leave the stats empty but marked current — seeding falls back to the first
            // authored row, and a later successful load recomputes them.
            _statsTexture = gamePath;
            _usedRowPairs.Clear();
            _rowUsageCounts.Clear();
            _sortedRowUsage.Clear();
            _statsTotalTexels = 1;
            return;
        }

        ComputeIdStats(gamePath, decoded);
    }

    private void ComputeIdStats(string gamePath, DecodedTexture decoded)
    {
        _statsTexture = gamePath;
        _usedRowPairs.Clear();
        _rowUsageCounts.Clear();
        for (var i = 0; i < decoded.Rgba.Length; i += 4)
        {
            _usedRowPairs.Add(IdMapTexel.Pair(decoded.Rgba[i]) + 1);
            var row = IdMapTexel.Row(decoded.Rgba[i], decoded.Rgba[i + 1]);
            _rowUsageCounts[row] = _rowUsageCounts.GetValueOrDefault(row) + 1;
        }

        // Prepared once per stats pass — the extraction list draws from these every frame.
        _sortedRowUsage.Clear();
        _sortedRowUsage.AddRange(_rowUsageCounts.OrderBy(kvp => kvp.Value).Select(kvp => (kvp.Key, kvp.Value)));
        _statsTotalTexels = Math.Max(1, decoded.Rgba.Length / 4);
    }

    private void Save(DTexture dTexture)
    {
        dTexture.LastEdit = DateTimeOffset.UtcNow;
        previewCache.Invalidate(dTexture.Identifier);
        _rowDiffuseMaterial = string.Empty; // resolved row colors refresh on next draw
        saveService.DelaySave(dTexture);
        overlayMods.QueueAutoApply(dTexture);
    }
}
