using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
using ImSharp;
using Luna;
using IService = Luna.IService;
using Penumbra.GameData.Files;
using Penumbra.GameData.Files.MaterialStructs;
using SixLabors.ImageSharp;
// Both ImSharp and ImageSharp define an Rgba32; this file's pixel work is ImageSharp's.
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

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
    SkinColorReader skinColorReader,
    HairColorReader hairColorReader)
    : IService, IDisposable
{
    private const long SlotPreviewDebounceMs = 400;

    /// <summary> Darkening applied to a shade-partner row: the benign blend target for a pair's unused half. </summary>
    private const float ShadeFactor = 0.6f;

    /// <summary>
    /// Colorset colors live in the game's SQUARED domain: the shader's display response is
    /// ~sqrt of the stored value (the same convention as the customize colors — see the
    /// PackSqrt pattern). Row edits store the squared value so authored-row roundtrips stay
    /// byte-exact (extraction); every picker and palette boundary converts through these, so
    /// the color the user picks is the color the game actually renders. Without this, decal
    /// colors written as display values rendered washed out in game (sqrt-brightened) while
    /// the preview showed them as picked.
    /// </summary>
    private static float[] DisplayToRowRgb(float r, float g, float b)
        => [r * r, g * g, b * b];

    private static Vector3 RowToDisplayRgb(IReadOnlyList<float> rgb)
        => new(MathF.Sqrt(MathF.Max(0f, rgb[0])), MathF.Sqrt(MathF.Max(0f, rgb[1])), MathF.Sqrt(MathF.Max(0f, rgb[2])));

    /// <summary> A row edit's diffuse packed as a display-domain Rgba32 (for presets/swatches). </summary>
    private static uint PackedDisplayDiffuse(ColorRowEdit row)
    {
        var display = RowToDisplayRgb(row.Diffuse);
        return new Rgba32(display.X, display.Y, display.Z).PackedValue;
    }

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

    private readonly ProceduralSurfaceSection _procSection = new();

    private string                             _statsTexture = string.Empty;
    private readonly HashSet<int>              _usedRowPairs = [];
    private readonly Dictionary<int, int>      _rowUsageCounts = [];
    private readonly List<(int Row, int Count)> _sortedRowUsage = [];
    private int                                _statsTotalTexels = 1;

    // Live customize colors, refreshed at most once a second. THE CHARACTER (Glamourer
    // included) is the source of truth for skin/hair colors — the preview always follows it.
    // Every successful read refreshes the config cache, which only serves as the fallback
    // while the character is unreadable (logged out, not human).
    // 0, NOT long.MinValue: TickCount64 - long.MinValue overflows negative, which made the
    // refresh condition permanently false — the live read never ran and everything silently
    // used the stored fallbacks.
    private HairColors? _liveHair;
    private long        _liveHairMs;
    private Vector3?    _liveSkin;
    private long        _liveSkinMs;

    private HairColors? LiveHair()
    {
        if (Environment.TickCount64 - _liveHairMs > 1000)
        {
            _liveHairMs = Environment.TickCount64;
            _liveHair   = hairColorReader.TryGetLocalPlayerHair(out var hair) ? hair : null;
            if (_liveHair is { } live)
            {
                var main      = new Rgba32(live.Main.X, live.Main.Y, live.Main.Z).PackedValue;
                var highlight = new Rgba32(live.Highlight.X, live.Highlight.Y, live.Highlight.Z).PackedValue;
                if (config.PreviewHairColor != main || config.PreviewHairHighlight != highlight)
                {
                    config.PreviewHairColor     = main;
                    config.PreviewHairHighlight = highlight;
                    config.Save();
                }
            }
        }

        return _liveHair;
    }

    private Vector3? LiveSkin()
    {
        if (Environment.TickCount64 - _liveSkinMs > 1000)
        {
            _liveSkinMs = Environment.TickCount64;
            _liveSkin   = skinColorReader.TryGetLocalPlayerSkin(out var tone) ? tone : null;
            if (_liveSkin is { } live)
            {
                var packed = new Rgba32(live.X, live.Y, live.Z).PackedValue;
                if (config.PreviewSkinTone != packed)
                {
                    config.PreviewSkinTone = packed;
                    config.Save();
                }
            }
        }

        return _liveSkin;
    }

    /// <summary> A small read-only swatch for a character-derived color. </summary>
    private static void DrawColorSwatch(string label, uint packed, bool live)
    {
        var color = new Rgba32(packed);
        // ColorButtonFlags does not surface NoTooltip, but the native color button honors it.
        Im.Color.Button(label, new System.Numerics.Vector4(color.R / 255f, color.G / 255f, color.B / 255f, 1f),
            (ColorButtonFlags)ColorEditorFlags.NoTooltip | ColorButtonFlags.NoAlpha);
        Im.Line.Same();
        Im.Text(label);
        if (Im.Item.Hovered())
            Im.Tooltip.OnHover(live
                ? "Read live from your character (Glamourer changes included). The game applies this in its shader — it is never baked into textures."
                : "Your character is not readable right now (not loaded, or not human) — showing the last known color.");
    }

    public void Dispose()
    {
        _viewport.Dispose();
        _texturePreviewWrap?.Dispose();
        _patternThumbnail.Wrap?.Dispose();
        DisposeGeneratedWraps();
    }

    /// <summary> The editing controls (left column): material selection, decal library, layers, per-kind sections. </summary>
    public void DrawControls(DTexture dTexture)
    {
        _highlightHovered = false;
        DrawInner(dTexture);
        if (!_highlightHovered)
            highlighter.Clear();
    }

    /// <summary>
    /// The visual column: the active material's composited texture above the 3D preview, both
    /// updating live with every edit. Must draw AFTER <see cref="DrawControls"/> in the frame —
    /// that call owns the per-selection state reset.
    /// </summary>
    public void DrawVisuals(DTexture dTexture)
    {
        if (_cacheOwner != dTexture.Identifier || dTexture.Data.Source.IsEmpty || _options is not { Count: > 0 })
            return;

        DrawTexturePreview(dTexture);
        Im.Separator();
        DrawViewport(dTexture);
    }

    /// <summary> Make a source material the active editing subject (used by the Source section's material rows). </summary>
    public void SelectMaterial(string materialGamePath)
        => _selectedMaterial = materialGamePath;

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
            Im.Text("Add a source first — open the Sources section."u8);
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
            // All slots kept: body overlay entries use the diffuse, hair companions their
            // normal + mask (the viewport renders the whole hairstyle from them).
            _overlayOptions = all.Where(o => overlayPaths.Contains(o.MaterialGamePath)).ToList();
        }

        if (_options.Count == 0)
        {
            Im.Text(dTexture.Data.Source.Materials.All(m => m.Overlay)
                ? "Only overlay-part sources (nails, accents) are selected — they're painted automatically by an overlapping body tattoo. Add the body itself to place one."u8
                : "The source materials expose no textures."u8);
            return;
        }

        DrawUnitSelector(dTexture);

        // Hair takes NO decals — card hair shares/mirrors texture regions between strands,
        // so any texel-space stamp repeats on strands it was never placed on (see the
        // hair-decal-uv-sharing project notes; the implementation is parked on branch
        // wip/hair-colorset-decals). The tab shows only the hair color context and the
        // Shine/Animated adjustments for hair materials.
        if (SelectedKind() is MaterialKind.Hair)
        {
            Im.TextWrapped(
                "Hair-shaded piece (hair, furred tail, ears) — the game blends your main hair color toward your highlight color per pixel, using the colors below (read live from your character)."u8);

            var liveHair = LiveHair();
            DrawColorSwatch("Hair", config.PreviewHairColor, liveHair != null);
            Im.Line.Same(0, 24 * Im.Style.GlobalScale);
            DrawColorSwatch("Highlights", config.PreviewHairHighlight, liveHair != null);

            if (liveHair is { HighlightsEnabled: false })
                Im.TextWrapped(
                    "Your character has highlights DISABLED — highlight edits stay invisible in-game (and in this preview) until you enable highlights in the aesthetician/character appearance."u8);
        }
        else
        {
            // Modern gear stamps the colorset id map; skin and legacy gear stamp the diffuse;
            // materials exposing neither (colorset-only legacy gear, vfx) stay gated off.
            if (DefaultTargetOption() == null)
            {
                var legacyIndex = MaterialOptions().Any(o => o is { Slot: TextureSlot.Index, DecalRecommended: false });
                Im.TextWrapped(legacyIndex
                    ? "This material has no color texture — its look comes entirely from its colorset, which decals cannot stamp onto yet."u8
                    : "This material exposes no texture decals can stamp onto."u8);
            }
            else
            {
                switch (SelectedKind())
                {
                    case MaterialKind.Skin:
                    {
                        Im.TextWrapped("Skin material — decals bake directly into the skin texture like tattoos and conform to the body."u8);
                        var liveSkin = LiveSkin();
                        DrawColorSwatch("Skin Color", config.PreviewSkinTone, liveSkin != null);
                        break;
                    }
                    case MaterialKind.LegacyDiffuse:
                        Im.TextWrapped("Legacy material — decal colors are baked into the color texture. Recoloring rebuilds the mod; dyes never affect the decal."u8);
                        break;
                }
            }

            Im.Separator();
            DrawDecalLibrary(dTexture);
            DrawProceduralAdd(dTexture);
            Im.Separator();
            DrawLayers(dTexture);
        }

        DrawHairSection(dTexture);
        DrawExtractionSection(dTexture);
        DrawStrayRows(dTexture);
        UpdateSlotPreview(dTexture);
    }


    private List<TextureOption>? _materialOptionsCache;
    private (List<TextureOption>? Options, string Material) _materialOptionsKey;

    // Unit grouping + label regexes redone only when the option list turns over (it is
    // rebuilt whenever the source materials change), not per frame.
    private List<(SourceUnit Unit, List<SourcePath> Parts)>? _unitSelectorCache;
    private List<TextureOption>?                             _unitSelectorKey;

    private MtrlFile?  _manageRowDiffuseMtrl;
    private Vector3[]? _manageRowDiffuse;

    /// <summary>
    /// The editing target selector: sources are MODEL units (the pieces added to this mod),
    /// so the dropdown lists those. A piece with several editable materials gets a Part
    /// dropdown next to it; the common single-material case stays one control.
    /// </summary>
    private void DrawUnitSelector(DTexture dTexture)
    {
        if (_unitSelectorCache == null || !ReferenceEquals(_unitSelectorKey, _options))
        {
            _unitSelectorKey   = _options;
            _unitSelectorCache = SourceUnits.Of(dTexture.Data.Source)
                .Select(u => (Unit: u, Parts: u.Materials
                    .Where(m => !m.Overlay && _options!.Any(o => string.Equals(o.MaterialGamePath, m.GamePath, StringComparison.OrdinalIgnoreCase)))
                    .ToList()))
                .Where(t => t.Parts.Count > 0)
                .ToList();
        }

        var units = _unitSelectorCache;
        if (units.Count == 0)
            return;

        var current = units.FirstOrDefault(t => t.Parts.Any(p
            => string.Equals(p.GamePath, _selectedMaterial, StringComparison.OrdinalIgnoreCase)));
        if (current.Unit == null)
        {
            current           = units[0];
            _selectedMaterial = current.Parts[0].GamePath;
        }

        Im.Item.SetNextWidthScaled(280);
        using (var combo = Im.Combo.Begin("##sourceUnit"u8, current.Unit.Label))
        {
            if (combo)
                foreach (var (unit, parts) in units)
                {
                    if (Im.Selectable($"{unit.Label}##{unit.Key}", ReferenceEquals(unit, current.Unit))
                     && !ReferenceEquals(unit, current.Unit))
                        _selectedMaterial = parts[0].GamePath;
                }
        }

        if (current.Parts.Count > 1)
        {
            Im.Line.Same();
            var currentPart = current.Parts.FirstOrDefault(p
                => string.Equals(p.GamePath, _selectedMaterial, StringComparison.OrdinalIgnoreCase)) ?? current.Parts[0];
            Im.Item.SetNextWidthScaled(180);
            using var combo = Im.Combo.Begin("##unitPart"u8, currentPart.Label);
            if (combo)
                foreach (var part in current.Parts)
                {
                    if (Im.Selectable($"{part.Label}##{part.GamePath}",
                            string.Equals(part.GamePath, _selectedMaterial, StringComparison.OrdinalIgnoreCase)))
                        _selectedMaterial = part.GamePath;
                }

            Im.Line.Same();
            LunaStyle.DrawHelpMarkerLabel("Part"u8,
                "This piece has several materials (parts with their own colorsets/textures) — pick which one to edit."u8);
        }
        else
        {
            Im.Line.Same();
            LunaStyle.DrawHelpMarkerLabel("Canvas"u8,
                "The piece being edited. Decals stamp onto its right texture automatically (the colorset id map on colorset-driven gear, else the color texture) and their material effects touch the normal/mask maps."u8);
        }
    }

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

    private TextureOption? NormalOption()
        => MaterialOptions().Find(o => o.Slot is TextureSlot.Normal);

    /// <summary>
    /// Where a new decal goes: modern gear prefers the colorset id map; skin, legacy and
    /// unknown materials take color decals on their diffuse. Hair takes NO decals: card hair
    /// reuses/mirrors texture regions across strands, so any texel-space stamp repeats on
    /// every strand sharing them (see the hair-decal-uv-sharing project notes; implementation
    /// parked on branch wip/hair-colorset-decals).
    /// </summary>
    private TextureOption? DefaultTargetOption()
        => SelectedKind() switch
        {
            MaterialKind.ModernColorset => IndexOption() ?? DiffuseOption(),
            MaterialKind.Hair           => null,
            _                           => DiffuseOption(),
        };

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

        Im.Separator();
        Im.TextWrapped($"Other edited rows on this material: {string.Join(", ", strays.Select(RowName))}");
        Im.TextWrapped("These affect the gear too — leftovers from removed decals or older experiments."u8);
        if (Im.SmallButton("Clear These Rows"u8) && Im.Io.KeyControl)
        {
            foreach (var row in strays)
                edit.Rows.Remove(row);
            if (edit.IsEmpty)
                dTexture.Data.Materials.Remove(_selectedMaterial);
            Save(dTexture);
        }

        if (Im.Item.Hovered())
            Im.Tooltip.OnHover("Hold Control and click to remove all listed row edits (the source values return)."u8);
    }

    private static string RowName(int row)
        => $"{row / 2 + 1}{(row % 2 == 0 ? 'A' : 'B')}";

    /// <summary> Eye icon that highlights a colorset row on the live model while hovered. </summary>
    private void DrawRowHighlightEye(TextureOption option, int row, ReadOnlySpan<byte> tooltip)
    {
        ImEx.Icon.Button((AwesomeIcon)Dalamud.Interface.FontAwesomeIcon.Eye, tooltip);
        if (Im.Item.Hovered())
        {
            _highlightHovered = true;
            highlighter.Highlight(option.MaterialGamePath, option.Mtrl, row);
        }
    }

    /// <summary> Debounced on-model preview of slot color/dye changes through a temporary mod. </summary>
    private void UpdateSlotPreview(DTexture dTexture)
    {
        if (!_slotPreviewDirty || _slotPreviewOption == null)
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
        using (Im.Disabled(!canAdd))
        {
            if (Im.Button("Add Decal from Library..."u8))
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

        Im.Tooltip.OnHover(canAdd
            ? "Pick a decal from the library — its saved settings (colors, surface finish, size) are applied automatically."u8
            : "Select a material that supports decals first."u8);

        Im.Line.Same();
        using (Im.Disabled(!canAdd))
        {
            if (Im.Button("Import Decal..."u8))
                _fileDialog.OpenFileDialog("Import Decal", "Images{.png,.jpg,.jpeg,.dds,.bmp,.tga}", (success, path) =>
                {
                    if (!success)
                        return;

                    var entry = decals.Import(path);
                    if (entry != null && _cacheOwner == dTexture.Identifier)
                        AddLayer(dTexture, entry.Id, entry.Preset);
                });
        }

        Im.Tooltip.OnHover("Import an image into the decal library and stamp it onto the selected material right away.\nTo import without stamping, use the Decal Library window (title-bar button)."u8);
    }

    /// <summary>
    /// Procedural surface layers cover the whole skin canvas (fur, scales, patterns) instead
    /// of stamping one image — offered on skin materials only, whose bodies/faces are uniquely
    /// unwrapped (card hair shares texels between strands and stays excluded).
    /// </summary>
    private void DrawProceduralAdd(DTexture dTexture)
    {
        if (SelectedKind() is not MaterialKind.Skin || DiffuseOption() is not { } target)
            return;

        if (Im.Button("Add Fur / Scales / Pattern"u8))
        {
            if (!dTexture.Data.Textures.TryGetValue(target.GamePath, out var layers))
            {
                layers                                  = [];
                dTexture.Data.Textures[target.GamePath] = layers;
            }

            CaptureTextureSource(dTexture, target.GamePath);
            layers.Add(new ProceduralSurfaceLayer());
            Save(dTexture);
        }

        Im.Tooltip.OnHover("Generates a full-body surface texture — fur, scales or a skin pattern — following the shape of the body."u8);
    }

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
            layer.ColorMerge      = preset.ColorMerge;
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
                    var row   = layer.PaletteRows[i];
                    GetOrSeedRow(edit, table, row).Diffuse = DisplayToRowRgb(color.R / 255f, color.G / 255f, color.B / 255f);
                    // Gradient pairs restore each half from its own preset entry; only a solo
                    // slot's B half carries the derived shade.
                    if (!layer.PaletteRows.Contains(row ^ 1))
                        GetOrSeedRow(edit, table, row + 1).Diffuse = DisplayToRowRgb(
                            color.R / 255f * ShadeFactor, color.G / 255f * ShadeFactor, color.B / 255f * ShadeFactor);
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
            ColorMerge      = decal.ColorMerge,
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
                    ? PackedDisplayDiffuse(rowEdit)
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
            Im.Text("No decals on this material yet — add one from the library above."u8);
    }

    private void DrawLayerList(DTexture dTexture, TextureOption option, List<TextureLayer> layers)
    {
        using var outerId = Im.Id.Push(option.GamePath);

        var remove = -1;
        var swap   = (-1, -1);
        foreach (var (idx, layer) in layers.Index())
        {
            using var id = Im.Id.Push(idx);
            if (layer is ProceduralSurfaceLayer proc)
            {
                DrawProceduralEntry(dTexture, proc, idx, layers.Count,
                    ModelUvReader.IsBodySkinMaterial(option.MaterialGamePath), ref remove, ref swap);
                continue;
            }

            if (layer is not DecalLayer decal)
                continue;

            var name = decal.LocalImageFile.Length > 0
                ? "Extracted decal"
                : decals.Get(decal.DecalId)?.Name ?? "(missing decal)";

            var enabled = decal.Enabled;
            if (Im.Checkbox("##enabled"u8, ref enabled))
            {
                decal.Enabled = enabled;
                // Re-enabling an auto-disabled colorset decal retries the row allocation.
                if (enabled && decal.IdRemap && decal.PaletteRows.Count == 0)
                    decal.RowError = null;
                Save(dTexture);
            }

            Im.Line.Same();
            var targetTag = option.Slot switch
            {
                TextureSlot.Index   => "  [colorset]",
                TextureSlot.Diffuse => string.Empty,
                _                   => $"  [{option.Slot}]",
            };
            var modeTag = decal.Surface
                ? decal is { AnchorX: 0f, AnchorY: 0f, AnchorZ: 0f } ? "  [3D — not placed!]" : "  [3D]"
                : string.Empty;
            var extractedTag = decal.Extracted && decal.LocalImageFile.Length == 0 ? "  [extracted]" : string.Empty;
            var errorTag     = decal.RowError != null ? "  [auto-disabled]" : string.Empty;
            if (!Im.Tree.Header($"{idx + 1}: {name}{targetTag}{extractedTag}{modeTag}{errorTag}###layer{idx}"))
                continue;

            using var indent = Im.Indent();

            var changed = false;
            if (decal.IdRemap)
                changed |= DrawIdRemapSettings(dTexture, option, decal);
            else
                changed |= DrawTintSettings(decal);

            changed |= DrawMaterialEffects(dTexture, option, decal);
            changed |= DrawPlacementSettings(dTexture, decal);

            if (changed)
                Save(dTexture);

            if (Im.SmallButton("Remove"u8))
                remove = idx;
            Im.Line.Same();
            if (Im.SmallButton("Up"u8) && idx > 0)
                swap = (idx, idx - 1);
            Im.Line.Same();
            if (Im.SmallButton("Down"u8) && idx < layers.Count - 1)
                swap = (idx, idx + 1);
            if (decal.LocalImageFile.Length == 0)
            {
                Im.Line.Same();
                if (Im.SmallButton("Save Settings to Library"u8))
                    SaveLayerPreset(dTexture, option, decal);
                Im.Tooltip.OnHover("Store this layer's colors, surface finish and size on the library entry.\nFuture attachments of this decal start from these settings — on any gear."u8);
            }
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
            // The temp stamp belongs to the layer — any library copy made from it stays.
            if (removedDecal is { } local && local.LocalImageFile.Length > 0)
                try
                {
                    File.Delete(decals.LayerImagePath(local));
                }
                catch (Exception ex)
                {
                    DynamicTextureManager.Log.Warning($"Could not delete extracted stamp {local.LocalImageFile}: {ex.Message}");
                }
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

    /// <summary> One procedural surface layer in the layer list: header row plus its settings. </summary>
    private void DrawProceduralEntry(DTexture dTexture, ProceduralSurfaceLayer proc, int idx, int count,
        bool bodyRegions, ref int remove, ref (int, int) swap)
    {
        var enabled = proc.Enabled;
        if (Im.Checkbox("##enabled"u8, ref enabled))
        {
            proc.Enabled = enabled;
            Save(dTexture);
        }

        Im.Line.Same();
        if (!Im.Tree.Header($"{idx + 1}: {ProceduralSurfaceSection.KindLabel(proc.Kind)}###layer{idx}"))
            return;

        using var indent = Im.Indent();

        if (_procSection.Draw(proc, bodyRegions))
            Save(dTexture);

        var activeChannel = _viewport.ActivePaintChannel(proc);

        var erasing = activeChannel == DecalViewport.PaintChannel.Coverage;
        if (Im.SmallButton(erasing ? "Erasing..."u8 : "Erase Areas"u8) && !erasing)
            _viewport.BeginCoveragePaint(proc, DecalViewport.PaintChannel.Coverage, () => Save(dTexture));
        Im.Tooltip.OnHover("Brush (or click Line points) over the 3D preview to remove the pattern where you don't want it — it tapers into bare skin."u8);

        Im.Line.Same();
        var marking = activeChannel == DecalViewport.PaintChannel.Markings;
        if (Im.SmallButton(marking ? "Marking..."u8 : "Paint Markings"u8) && !marking)
        {
            if (proc.Markings != FurMarkingStyle.Painted)
            {
                proc.Markings = FurMarkingStyle.Painted;
                Save(dTexture);
            }

            _viewport.BeginCoveragePaint(proc, DecalViewport.PaintChannel.Markings, () => Save(dTexture));
        }

        Im.Tooltip.OnHover("Paint the highlight color onto the coat — brush freely, or use Line to click a stripe along the back. Switches Markings to Painted."u8);

        if (proc.MaskDabs.Count > 0 || proc.MarkingDabs.Count > 0)
        {
            Im.Line.Same();
            Im.Text($"({proc.MaskDabs.Count} erase, {proc.MarkingDabs.Count} marking dabs)");
        }

        if (Im.SmallButton("Remove"u8))
            remove = idx;
        Im.Line.Same();
        if (Im.SmallButton("Up"u8) && idx > 0)
            swap = (idx, idx - 1);
        Im.Line.Same();
        if (Im.SmallButton("Down"u8) && idx < count - 1)
            swap = (idx, idx + 1);
    }

    /// <summary>
    /// A colorset decal renders through automatically claimed colorset rows: the image is
    /// quantized to at most Max Colors, blend-compatible colors share one slot as a gradient
    /// pair (A = lighter, B = darker, per-texel G blends between them), and the rest claim a
    /// slot alone. This editor bundles the color list, dye behavior and shape threshold —
    /// the one place all colorset settings live.
    /// </summary>
    private bool DrawIdRemapSettings(DTexture dTexture, TextureOption option, DecalLayer decal)
    {
        if (option.Mtrl.Table is not ColorTable table)
            return false;

        EnsureIdStats(dTexture, option.GamePath);
        var changed = false;

        // Old saves and layers whose allocation was cleared claim their rows on first draw.
        // Saves from older schemes (a slot shared with another decal, or a B half claimed
        // WITHOUT its A partner) fringe at decal edges — heal them by reallocating onto
        // whole, exclusively owned slots. A B half whose A half the same decal owns is a
        // gradient pair, not a conflict. Extracted layers own gear-authored rows and are
        // never re-quantized onto new ones.
        if (decal is { Extracted: false, Enabled: true, RowError: null })
        {
            var conflict = decal.PaletteRows.Count > 0
             && (decal.PaletteRows.Any(r => r % 2 == 1 && !decal.PaletteRows.Contains(r ^ 1))
                 || (ClaimedRowsForMaterial(dTexture, option.MaterialGamePath, decal) is var otherRows
                     && decal.PaletteRows.Any(otherRows.Contains)));
            if (decal.PaletteRows.Count == 0 || conflict)
                changed |= ReallocateDecal(dTexture, option, table, decal);
        }

        if (decal.Extracted)
        {
            Im.TextWrapped("Extracted from this texture's id map — relocated onto its own claimed slots, seeded from the source rows."u8);
            Im.Tooltip.OnHover(
                "This decal was lifted out of the id map and moved onto freshly claimed colorset slots that copy the source rows' authored look.\nRecoloring a slot recolors only the decal — the rest of the gear keeps its own rows."u8);

            if (decal.LocalImageFile.Length > 0)
            {
                var libraryCopy = decal.LibraryCopyId != Guid.Empty ? decals.Get(decal.LibraryCopyId) : null;
                if (libraryCopy != null)
                {
                    Im.Text($"In library as \"{libraryCopy.Name}\".");
                }
                else
                {
                    if (Im.SmallButton("Add to Library"u8))
                        AddExtractedToLibrary(dTexture, option, decal);
                    Im.Tooltip.OnHover("Keep a copy in the decal library for use on other gear."u8);
                }
            }
        }
        else
        {
            Im.Item.SetNextWidthScaled(220);
            var merge = decal.ColorMerge;
            if (Im.Slider("Color Merge"u8, ref merge, "%.0f"u8, 4f, 64f))
                decal.ColorMerge = Math.Clamp(merge, 4f, 64f);
            if (Im.Item.DeactivatedAfterEdit)
                changed |= ReallocateDecal(dTexture, option, table, decal);
            Im.Tooltip.OnHover(
                "The decal picks how many colors it needs on its own: the fewest whose blended rendering still matches the image within this distance. Raise to merge similar colors harder (fewer slots), lower to keep more apart.\nColors that blend cleanly (shades of one hue, black/white, outline + fill) share one colorset slot as a gradient pair, keeping the decal's smooth shading and anti-aliasing; unrelated hues claim a whole slot each."u8);

            Im.Line.Same();
            Im.Text($"Colors: {decal.PaletteColors.Count} (auto)");

            Im.Line.Same();
            if (Im.SmallButton("Re-extract Colors"u8))
                changed |= ReallocateDecal(dTexture, option, table, decal);
            Im.Tooltip.OnHover("Quantize the decal image again and reassign rows — discards manual recolors below."u8);
        }

        if (decal.RowError != null)
        {
            using var color = ImGuiColor.Text.Push(new ImSharp.Rgba32(0xFF00A0FFu));
            Im.TextWrapped(decal.RowError);
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
            using var id       = Im.Id.Push(i);
            var       row      = decal.PaletteRows[i];
            var       rowEdit  = rows[i];
            var       source   = i < decal.PaletteColors.Count ? new Rgba32(decal.PaletteColors[i]) : new Rgba32(255, 255, 255);

            Im.Color.Button("##extracted"u8, new Vector4(source.R / 255f, source.G / 255f, source.B / 255f, 1f));
            if (Im.Item.Hovered())
                Im.Tooltip.OnHover("The color extracted from the decal image — image pixels closest to it render through this row."u8);

            Im.Line.Same();
            // The picker edits the DISPLAY color; the row stores its square (colorset domain).
            var color = RowToDisplayRgb(rowEdit.Diffuse);
            Im.Item.SetNextWidthScaled(250);
            // Gradient pairs render two of the decal's colors on one slot's halves — each is
            // its own editable color, so no shade sync (that would clobber the partner).
            var partnered = decal.PaletteRows.Contains(row ^ 1);
            var label     = partnered ? $"Slot {row / 2 + 1}{(row % 2 == 0 ? "A" : "B")}" : $"Slot {row / 2 + 1}";
            if (Im.Color.Editor(label, ref color, ColorEditorFlags.Float))
            {
                rowEdit.Diffuse = DisplayToRowRgb(color.X, color.Y, color.Z);
                // Keep a solo slot's B row a darkened copy so the baked shading blend darkens.
                if (!partnered)
                    GetOrSeedRow(edit, table, row + 1).Diffuse =
                        DisplayToRowRgb(color.X * ShadeFactor, color.Y * ShadeFactor, color.Z * ShadeFactor);
                changed = true;
            }

            if (Im.Item.Hovered())
                Im.Tooltip.OnHover("This part of the decal renders in this color — recolor it without touching the image."u8);

            Im.Line.Same();
            DrawRowHighlightEye(option, row,
                "Highlights the parts of the model this row colors while hovered (redraws your character).\nAfter a build, that includes the decal itself."u8);
        }

        if (Im.SmallButton("Reset Rows"u8))
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

        if (Im.Item.Hovered())
            Im.Tooltip.OnHover("Rebuild the claimed rows from the gear's own authored values (keeps your colors).\nUse this after plugin updates or if the decal renders black or washed out from older edits."u8);

        // Dye: one switch across all claimed rows with a smart default copied from how the
        // rest of the gear dyes.
        var lead    = rows[0];
        var dyeable = lead.DyeMode == ColorRowEdit.RowDyeMode.Custom;
        if (Im.Checkbox("Dyeable"u8, ref dyeable))
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
            Im.Line.Same();
            var channel = lead.DyeChannel + 1;
            Im.Item.SetNextWidthScaled(100);
            if (Im.Slider("Dye Channel"u8, ref channel, "%d"u8, 1, 2))
            {
                foreach (var row in claimedRows)
                    row.DyeChannel = (byte)(channel - 1);
                changed = true;
            }

            Im.Line.Same();
            var template = (int)lead.DyeTemplate;
            Im.Item.SetNextWidthScaled(100);
            if (Im.Input.Scalar("Dye Template"u8, ref template) && template is >= 0 and <= 2047)
            {
                foreach (var row in claimedRows)
                    row.DyeTemplate = (ushort)template;
                changed = true;
            }

            Im.Tooltip.OnHover(
                "How stain colors translate to the claimed rows — detected from how the rest of this gear dyes.\nIf it reads 0, no template was detected; copy the id from a similar dyeable item."u8);

            Im.TextWrapped(lead.DyeTemplate > 0
                ? $"Dyes like the rest of this gear (template {lead.DyeTemplate})."
                : "No dye template detected on this gear — the decal will not react to dyes until a template id is set above.");
        }
        else
        {
            Im.Text("The decal keeps its colors when the gear is dyed."u8);
        }

        Im.Item.SetNextWidthScaled(220);
        changed |= Im.Slider("Shape Threshold"u8, ref decal.AlphaThreshold, "%.2f"u8, 0.05f, 1f);
        if (!decal.Extracted && Im.Item.DeactivatedAfterEdit)
            changed |= ReallocateDecal(dTexture, option, table, decal);
        Im.Tooltip.OnHover(decal.Extracted
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
        if (Im.Checkbox("Recolor Decal"u8, ref tintEnabled))
        {
            decal.TintEnabled = tintEnabled;
            if (tintEnabled && !decal.HasTint && ExtractTintPalette(decal))
                decal.TintColors = decal.PaletteColors.ToList();
            changed = true;
        }

        Im.Tooltip.OnHover(
            "Extracts the decal's main colors and lets each be replaced — the recolors are baked into the texture on the next build.\nOff, the decal keeps its original image colors."u8);

        if (!decal.TintEnabled)
            return changed;

        Im.Item.SetNextWidthScaled(220);
        var merge = decal.ColorMerge;
        if (Im.Slider("Color Merge"u8, ref merge, "%.0f"u8, 4f, 64f))
            decal.ColorMerge = Math.Clamp(merge, 4f, 64f);
        if (Im.Item.DeactivatedAfterEdit && ExtractTintPalette(decal))
        {
            decal.TintColors = decal.PaletteColors.ToList();
            changed          = true;
        }

        Im.Tooltip.OnHover(
            "The decal picks how many colors it needs on its own: the fewest whose blended rendering still matches the image within this distance.\nRaise to merge similar colors harder, lower to keep more apart. Changing it re-extracts the colors and discards the recolors below."u8);

        Im.Line.Same();
        Im.Text($"Colors: {decal.PaletteColors.Count} (auto)");

        Im.Line.Same();
        if (Im.SmallButton("Re-extract Colors"u8) && ExtractTintPalette(decal))
        {
            decal.TintColors = decal.PaletteColors.ToList();
            changed          = true;
        }

        Im.Tooltip.OnHover("Quantize the decal image again — discards the recolors below."u8);

        if (decal.PaletteColors.Count == 0)
        {
            Im.TextWrapped("Could not extract any colors from the decal image — is the extraction threshold too high?"u8);
            return changed;
        }

        // One editable color per extracted color; the extracted swatch stays as reference so
        // recoloring never loses which image color it replaces.
        for (var i = 0; i < decal.PaletteColors.Count && i < decal.TintColors.Count; ++i)
        {
            using var id     = Im.Id.Push(i);
            var       source = new Rgba32(decal.PaletteColors[i]);

            Im.Color.Button("##extracted"u8, new Vector4(source.R / 255f, source.G / 255f, source.B / 255f, 1f));
            if (Im.Item.Hovered())
                Im.Tooltip.OnHover("The color extracted from the decal image — image pixels closest to it render in the replacement color."u8);

            Im.Line.Same();
            var tint  = new Rgba32(decal.TintColors[i]);
            var color = new Vector3(tint.R / 255f, tint.G / 255f, tint.B / 255f);
            Im.Item.SetNextWidthScaled(250);
            if (Im.Color.Editor($"Color {i + 1}", ref color, ColorEditorFlags.Float))
                decal.TintColors[i] = new Rgba32(color.X, color.Y, color.Z).PackedValue;
            // A save rebuilds the mod's textures — commit once the edit ends, not per drag frame.
            if (Im.Item.DeactivatedAfterEdit)
                changed = true;

            if (Im.Item.Hovered())
                Im.Tooltip.OnHover("This part of the decal renders in this color — recolor it without touching the image."u8);
        }

        Im.Item.SetNextWidthScaled(220);
        Im.Slider("Extraction Threshold"u8, ref decal.AlphaThreshold, "%.2f"u8, 0.05f, 1f);
        if (Im.Item.DeactivatedAfterEdit)
        {
            if (ExtractTintPalette(decal))
                decal.TintColors = decal.PaletteColors.ToList();
            changed = true;
        }

        Im.Tooltip.OnHover(
            "Decal pixels whose alpha is at or above this value feed the color extraction.\nBlending keeps the image's soft edges either way — this only affects which pixels count as colors."u8);

        return changed;
    }

    /// <summary> Quantize the decal image into the palette a tint maps against. Returns false when nothing usable was extracted. </summary>
    private bool ExtractTintPalette(DecalLayer decal)
    {
        var path = decals.LayerImagePath(decal);
        if (!File.Exists(path))
            return false;

        try
        {
            decal.PaletteColors = DecalQuantizer.ExtractPaletteAuto(path, decal.AlphaThreshold, decal.ColorMerge).ToList();
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

        var path = decals.LayerImagePath(decal);
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
            palette = DecalQuantizer.ExtractPaletteAuto(path, decal.AlphaThreshold, decal.ColorMerge);
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
            // Blend-compatible colors share one slot (gradient pair: lighter color on the A
            // half, darker on B, per-texel G carries the decal's own gradient between them),
            // so the decal claims the minimum number of slots and keeps its anti-aliasing.
            var groups = ColorRowAllocator.GroupGradientPairs(palette);
            var result = ColorRowAllocator.Allocate(groups.Count, EffectiveGearUsedPairs(dTexture, option.MaterialGamePath), others);
            decal.RowError = result.Error;
            if (result.Success)
            {
                var rowByColor = new int[palette.Length];
                for (var g = 0; g < groups.Count; ++g)
                {
                    var rowA = result.Rows[g];
                    rowByColor[groups[g].Light] = rowA;
                    if (groups[g].Dark >= 0)
                        rowByColor[groups[g].Dark] = rowA + 1;

                    var light = new Rgba32(palette[groups[g].Light]);
                    edit.Rows.Remove(rowA);
                    edit.Rows.Remove(rowA + 1);
                    GetOrSeedRow(edit, table, rowA).Diffuse = DisplayToRowRgb(light.R / 255f, light.G / 255f, light.B / 255f);

                    // A gradient pair's B row carries its own real color; a solo slot's B row
                    // gets a darkened copy — the id map's G channel blends A toward B exactly
                    // where the garment baked its cloth shading, so the shading stays visible
                    // on the decal.
                    var dark = groups[g].Dark >= 0
                        ? new Rgba32(palette[groups[g].Dark])
                        : new Rgba32((byte)(light.R * ShadeFactor), (byte)(light.G * ShadeFactor), (byte)(light.B * ShadeFactor));
                    GetOrSeedRow(edit, table, rowA + 1).Diffuse = DisplayToRowRgb(dark.R / 255f, dark.G / 255f, dark.B / 255f);
                }

                decal.PaletteRows = rowByColor.ToList();

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
        Im.Separator();
        Im.Text("Material Effects"u8);
        Im.Tooltip.OnHover("The decal's footprint replayed onto the material's other textures — smoothing bump detail or changing the surface finish under the decal."u8);

        if (hasNormal)
        {
            Im.Item.SetNextWidthScaled(220);
            var smooth = decal.NormalSmooth;
            if (Im.Slider("Normal Smoothing"u8, ref smooth, "%.2f"u8, 0f, 1f))
            {
                decal.NormalSmooth = Math.Clamp(smooth, 0f, 1f);
                changed            = true;
            }

            Im.Tooltip.OnHover("Flattens the cloth/skin bump detail under the decal — like a print sitting on top of the fabric.\n0 leaves the normal map untouched."u8);
        }

        if (showFinish)
        {
            Im.Item.SetNextWidthScaled(220);
            using (var combo = Im.Combo.Begin("Surface Finish"u8, FinishLabel(decal.Finish)))
            {
                if (combo)
                    foreach (var mode in DecalFinishMode.Values)
                    {
                        if (!Im.Selectable(FinishLabel(mode), mode == decal.Finish) || mode == decal.Finish)
                            continue;

                        decal.Finish  = mode;
                        finishChanged = true;
                    }
            }

            Im.Tooltip.OnHover(decal.IdRemap
                ? "How the surface responds to light under the decal — written into the claimed colorset rows (and the mask map, if the material has one).\nMatte suits cloth prints, Glossy suits stickers/vinyl; Custom exposes the raw values."u8
                : "How the surface responds to light under the decal, written into the material's mask map.\nMatte suits cloth prints, Glossy suits stickers/vinyl; Custom exposes the raw values.\nNote: on colorset-driven gear the underlying rows bound what the mask alone can change."u8);

            if (decal.Finish == DecalFinishMode.Custom)
            {
                Im.Item.SetNextWidthScaled(220);
                var roughness = decal.FinishRoughness;
                if (Im.Slider("Roughness"u8, ref roughness, "%.2f"u8, 0f, 1f))
                {
                    decal.FinishRoughness = Math.Clamp(roughness, 0f, 1f);
                    finishChanged         = true;
                }

                Im.Tooltip.OnHover("0 = mirror-glossy, 1 = fully matte."u8);

                Im.Item.SetNextWidthScaled(220);
                var specScale = decal.FinishSpecScale;
                if (Im.Slider("Specular Scale"u8, ref specScale, "%.2f"u8, 0f, 2f))
                {
                    decal.FinishSpecScale = Math.Clamp(specScale, 0f, 2f);
                    finishChanged         = true;
                }

                Im.Tooltip.OnHover("Multiplier on the authored specular color — below 1 dims reflections, above 1 boosts them."u8);
            }
            else if (decal.Finish != DecalFinishMode.Keep)
            {
                var (roughness, specScale) = FinishMapping.PresetValues(decal.Finish);
                using var disabled = Im.Disabled();
                Im.Text($"Roughness {roughness:F2}, specular ×{specScale:F2}");
            }
        }

        if (decal.HasMaterialEffects && (hasNormal || hasMask))
        {
            Im.Item.SetNextWidthScaled(220);
            var effectScale = decal.EffectScale;
            if (Im.Slider("Effect Scale"u8, ref effectScale, "%.2f"u8, 0.25f, 3f))
            {
                decal.EffectScale = Math.Clamp(effectScale, 0.25f, 3f);
                changed           = true;
            }

            Im.Tooltip.OnHover("Size of the affected area relative to the decal — above 1 the smoothing/finish extends past the decal's edge, below 1 it stays inside it."u8);
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
        if (Im.Checkbox("Place on Model (3D)"u8, ref surface))
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

        Im.Tooltip.OnHover(
            "Project the decal onto the 3D mesh instead of stamping it flat into the texture.\nIt conforms to the surface, keeps a real-world size and continues across UV seams."u8);

        if (decal.Surface)
        {
            var widthCm = decal.WorldWidth * 100f;
            Im.Item.SetNextWidthScaled(220);
            if (Im.Slider("Width (cm)"u8, ref widthCm, "%.1f"u8, 1f, 100f))
            {
                decal.WorldWidth = widthCm / 100f;
                changed          = true;
            }

            var heightCm = decal.WorldHeight * 100f;
            Im.Item.SetNextWidthScaled(220);
            if (Im.Slider("Height (cm)"u8, ref heightCm, "%.1f"u8, 1f, 100f))
            {
                decal.WorldHeight = heightCm / 100f;
                changed           = true;
            }

            Im.Item.SetNextWidthScaled(220);
            changed |= Im.Slider("Rotation"u8, ref decal.RotationDeg, "%.1f°"u8, -180f, 180f);
            if (!decal.IdRemap)
            {
                Im.Item.SetNextWidthScaled(220);
                changed |= Im.Slider("Opacity"u8, ref decal.Opacity, "%.2f"u8, 0f, 1f);
            }

            var limitToPart = decal.SurfaceLimitToPart;
            if (Im.Checkbox("Limit to Clicked Mesh Part"u8, ref limitToPart))
            {
                decal.SurfaceLimitToPart = limitToPart;
                changed                  = true;
            }

            Im.Tooltip.OnHover(
                "Keep the projection on the mesh piece you stamped it on.\nWithout this, overlapping pieces (linings, straps, panels behind) within reach catch the decal too."u8);

            if (Im.Button(_viewport.IsOpenFor(decal) ? "Stop Placing"u8 : "Place in 3D View"u8))
            {
                if (_viewport.IsOpenFor(decal))
                    _viewport.EndPlacement();
                else
                    BeginPlacement(dTexture, decal);
            }

            Im.Tooltip.OnHover(
                "Bind this decal to the 3D preview below — stamp and drag it directly on the mesh,\norbit and zoom freely, and changes apply to the mod when you finish an adjustment."u8);

            if (_placementError.Length > 0)
                using (ImGuiColor.Text.Push(new ImSharp.Rgba32(0xFF00A0FFu)))
                    Im.TextWrapped(_placementError);
            else if (decal is { AnchorX: 0f, AnchorY: 0f, AnchorZ: 0f })
                using (ImGuiColor.Text.Push(new ImSharp.Rgba32(0xFF00A0FFu)))
                    Im.TextWrapped("NOT PLACED YET — the decal stays invisible until you place it in the 3D view below."u8);
        }
        else
        {
            Im.Item.SetNextWidthScaled(220);
            changed |= Im.Slider("Position U"u8, ref decal.PosU, "%.3f"u8, 0f, 1f);
            Im.Item.SetNextWidthScaled(220);
            changed |= Im.Slider("Position V"u8, ref decal.PosV, "%.3f"u8, 0f, 1f);
            Im.Item.SetNextWidthScaled(220);
            changed |= Im.Slider("Scale X"u8, ref decal.ScaleX, "%.3f"u8, 0.01f, 1f);
            Im.Item.SetNextWidthScaled(220);
            changed |= Im.Slider("Scale Y"u8, ref decal.ScaleY, "%.3f"u8, 0.01f, 1f);
            Im.Item.SetNextWidthScaled(220);
            changed |= Im.Slider("Rotation"u8, ref decal.RotationDeg, "%.1f°"u8, -180f, 180f);
            if (!decal.IdRemap)
            {
                Im.Item.SetNextWidthScaled(220);
                changed |= Im.Slider("Opacity"u8, ref decal.Opacity, "%.2f"u8, 0f, 1f);
            }

            if (Im.SmallButton("Flip H"u8))
            {
                decal.FlipX = !decal.FlipX;
                changed     = true;
            }

            Im.Tooltip.OnHover("Mirror the decal horizontally."u8);
            Im.Line.Same();
            if (Im.SmallButton("Flip V"u8))
            {
                decal.FlipY = !decal.FlipY;
                changed     = true;
            }

            Im.Tooltip.OnHover("Mirror the decal vertically."u8);

            Im.TextWrapped("Flat UV placement — check the result in the 3D preview below or the Textures tab, or switch to Place on Model (3D)."u8);
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
        _viewport.BeginPlacement(decal, decals.LayerImagePath(decal), () => Save(dTexture));
    }

    #endregion

    #region 3D preview shading

    private readonly record struct ShadingKey(int DiffuseVersion, int IndexVersion, int RowVersion, bool Placement, uint SkinTone,
        uint HairColor, uint HairHighlight, int HairMaskVersion, int OverlayVersionHash, ViewportEffect? Effect,
        int NormalMapVersion);

    // Effect pattern pixels for the live viewport effect and thumbnails, cached per
    // (pattern, library entry) — ViewportEffect compares the array by reference, so the same
    // selection must return the same instance. The viewport's effect sampler expects a
    // SQUARE pattern, so image-based sources (library import, the game's glitter texture)
    // are resampled to a square here; the UV mapping is identical either way (the shader
    // tiles 0..1 regardless of texel dimensions), the build ships the original data.
    private (int Pattern, Guid LibraryId, byte[] Pixels, int Size) _effectPatternCache = (-1, Guid.Empty, [], 0);

    private (byte[] Pixels, int Size) EffectPatternPixels(AnimatedHairEdit edit)
    {
        if (_effectPatternCache.Pattern == edit.Pattern
         && _effectPatternCache.LibraryId == edit.EffectLibraryId
         && _effectPatternCache.Pixels.Length > 0)
            return (_effectPatternCache.Pixels, _effectPatternCache.Size);

        byte[]? source = null;
        var sourceW = 0;
        var sourceH = 0;
        if (edit.EffectLibraryId != Guid.Empty)
            try
            {
                var file = decals.EffectFilePath(edit.EffectLibraryId);
                if (File.Exists(file))
                {
                    using var image = Image.Load<Rgba32>(file);
                    source  = new byte[image.Width * image.Height * 4];
                    sourceW = image.Width;
                    sourceH = image.Height;
                    image.CopyPixelDataTo(source);
                }
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Warning($"Could not load library effect pattern {edit.EffectLibraryId}: {ex.Message}");
            }
        else if ((AnimatedHairBuilder.HairEffectPattern)edit.Pattern is AnimatedHairBuilder.HairEffectPattern.DressGlitter
         && textureIO.Load(AnimatedHairBuilder.DressGlitterTexPath, null, null) is { } glitter)
        {
            source  = glitter.Rgba;
            sourceW = glitter.Width;
            sourceH = glitter.Height;
        }

        byte[] pixels;
        int    size;
        if (source != null)
        {
            size   = AnimatedHairBuilder.PatternSize;
            pixels = ResampleSquare(source, sourceW, sourceH, size);
        }
        else
        {
            var pattern = (AnimatedHairBuilder.HairEffectPattern)edit.Pattern;
            if (pattern is AnimatedHairBuilder.HairEffectPattern.DressGlitter)
                pattern = AnimatedHairBuilder.HairEffectPattern.Shimmer;
            size   = AnimatedHairBuilder.PatternDimension(pattern);
            pixels = AnimatedHairBuilder.GeneratePattern(pattern, size);
        }

        _effectPatternCache = (edit.Pattern, edit.EffectLibraryId, pixels, size);
        return (pixels, size);
    }

    private static byte[] ResampleSquare(byte[] rgba, int width, int height, int size)
    {
        var result = new byte[size * size * 4];
        for (var y = 0; y < size; ++y)
        {
            var sy = Math.Min(height - 1, y * height / size);
            for (var x = 0; x < size; ++x)
            {
                var sx = Math.Min(width - 1, x * width / size);
                Array.Copy(rgba, (sy * width + sx) * 4, result, (y * size + x) * 4, 4);
            }
        }

        return result;
    }

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
            Im.TextWrapped(source != null && ModelUvReader.IsBodySkinMaterial(source.GamePath)
                ? "Your current body does not use this skin material — run Load Skin in the Source tab again and add the body material listed there."u8
                : "No mesh geometry available for a 3D preview — re-add the material in the Source tab while wearing the gear."u8);
            return;
        }

        // A bound placement or paint layer must belong to the selected material — switching
        // materials would otherwise pair its overlay with the wrong mesh and shading.
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
        var kind = SelectedKind();
        // Hair has no diffuse — the composited NORMAL map is the shading entry; the viewport
        // blends the preview hair colors by its blue channel and cuts out by its alpha.
        var diffuseOption = kind is MaterialKind.Hair ? NormalOption() : DiffuseOption();
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
        // color — stand in with the character's live tone so the preview resembles skin.
        if (kind is MaterialKind.Skin)
            LiveSkin();
        var skinTone = kind is MaterialKind.Skin ? config.PreviewSkinTone : 0u;

        // Hair preview colors; when the character's highlights are disabled the game shows no
        // highlight blend at all, so collapse the preview the same way (the header explains).
        // The hair mask's alpha (ambient occlusion) additionally shades the strands.
        var (hairColor, hairHighlight) = HairPreviewColorsPacked(dTexture, kind);
        var maskEntry = kind is MaterialKind.Hair
            ? EntryFor(MaterialOptions().Find(o => o.Slot is TextureSlot.Mask))
            : null;

        // Converted hair: the scrolling effect renders live in the viewport, over the
        // highlight areas of every hair mesh — or over the WHOLE piece when its normal has
        // no highlight channel (tails; same detection the build uses on the same composited
        // buffer). The stored effect color lives in the squared colorset domain — take the
        // root (intensity folded in) so the glow's on-screen brightness matches the in-game
        // emissive.
        ViewportEffect? viewportEffect = null;
        if (kind is MaterialKind.Hair
         && dTexture.Data.AnimatedHair.GetValueOrDefault(_selectedMaterial) is { Enabled: true } animatedEdit)
        {
            var (patternPixels, patternSize) = EffectPatternPixels(animatedEdit);
            viewportEffect = new ViewportEffect(patternPixels, patternSize,
                new Vector3(
                    MathF.Sqrt(Math.Clamp(animatedEdit.EffectColor[0] * animatedEdit.EffectIntensity, 0f, 1f)),
                    MathF.Sqrt(Math.Clamp(animatedEdit.EffectColor[1] * animatedEdit.EffectIntensity, 0f, 1f)),
                    MathF.Sqrt(Math.Clamp(animatedEdit.EffectColor[2] * animatedEdit.EffectIntensity, 0f, 1f))),
                animatedEdit.ScrollU, animatedEdit.ScrollV,
                animatedEdit.TilingU, animatedEdit.TilingV,
                EffectFullCoverage(diffuseEntry));
        }

        // Extra meshes rendered alongside the primary — body overlay parts (nails, accents)
        // or the other hair materials of the same hair model — each with its own composited
        // texture from the SAME preview cache the build writes through, so the live preview
        // matches the built result, including mid-drag.
        var overlayEntries = BuildOverlayEntries(dTexture, placementLayer, boundPath, out var overlayVersionHash);

        // The composited normal map shades the preview's relief (fur strands, scales, decal
        // smoothing) — hair excluded, its normal already IS the color-shading entry.
        var normalMapEntry = kind is MaterialKind.Hair ? null : EntryFor(NormalOption());

        var key = new ShadingKey(diffuseEntry?.Version ?? -1, indexEntry?.Version ?? -1, _rowDiffuseVersion,
            placementLayer != null, skinTone, hairColor, hairHighlight, maskEntry?.Version ?? -1, overlayVersionHash,
            viewportEffect, normalMapEntry?.Version ?? -1);
        if (key == _shadingKey)
            return;

        _shadingKey = key;

        Vector3? tone = null;
        if (skinTone != 0u)
        {
            var packed = new Rgba32(skinTone);
            tone = new Vector3(packed.R / 255f, packed.G / 255f, packed.B / 255f);
        }

        _viewport.UpdateShading(new ViewportShading(PreviewBuffer(diffuseEntry), PreviewBuffer(indexEntry), _rowDiffuse, tone,
            HairPreviewColors(dTexture, kind), PreviewBuffer(maskEntry), viewportEffect, PreviewBuffer(normalMapEntry)));
        _viewport.SetOverlays(overlayEntries);
    }

    // Flat-highlight-channel detection over the composited normal, cached per entry version —
    // the viewport's twin of the build-side coverage decision (AnimatedHairBuilder.
    // IsFlatHighlightChannel), so the preview glows exactly where the built id map routes.
    private (string Material, int Version, bool Flat) _effectCoverageCache = (string.Empty, -1, false);

    private bool EffectFullCoverage(CompositePreviewCache.Entry? normalEntry)
    {
        var buffer = normalEntry?.Composited ?? normalEntry?.Pristine?.Rgba;
        if (normalEntry == null || buffer == null)
            return false;

        if (!string.Equals(_effectCoverageCache.Material, _selectedMaterial, StringComparison.OrdinalIgnoreCase)
         || _effectCoverageCache.Version != normalEntry.Version)
            _effectCoverageCache = (_selectedMaterial, normalEntry.Version, AnimatedHairBuilder.IsFlatHighlightChannel(buffer));

        return _effectCoverageCache.Flat;
    }

    private static DecodedTexture? PreviewBuffer(CompositePreviewCache.Entry? entry)
        => entry?.Pristine == null
            ? null
            : entry.Composited != null
                ? new DecodedTexture(entry.Composited, entry.Pristine.Width, entry.Pristine.Height)
                : entry.Pristine;

    /// <summary> The effective packed preview hair colors (highlight collapsed to main while disabled), 0 for non-hair. </summary>
    private (uint Main, uint Highlight) HairPreviewColorsPacked(DTexture dTexture, MaterialKind kind)
    {
        if (kind is not MaterialKind.Hair)
            return (0u, 0u);

        // Converted (animated) hair: its colorset colors replace the character colors and the
        // Highlights toggle entirely — preview the base color with the effect color in the
        // highlight areas. Static stand-in; the scrolling only shows in game. Colorset colors
        // live in the squared domain, so take the root here — the renderer squares them back.
        var live = LiveHair();
        if (dTexture.Data.AnimatedHair.GetValueOrDefault(_selectedMaterial) is { Enabled: true } animated)
        {
            // Hair and highlight diffuse only — the glow itself renders as a live animated
            // overlay in the viewport (ViewportEffect), not baked into these colors.
            var (baseColor, highlightColor) = EffectiveAnimatedColors(animated);
            return (PackSqrt(baseColor, 1f), PackSqrt(highlightColor, 1f));
        }

        return (config.PreviewHairColor,
            live is { HighlightsEnabled: false } ? config.PreviewHairColor : config.PreviewHairHighlight);
    }

    private static uint PackSqrt(float[] rgb, float scale)
        => new Rgba32(
            MathF.Sqrt(Math.Clamp(rgb[0] * scale, 0f, 1f)),
            MathF.Sqrt(Math.Clamp(rgb[1] * scale, 0f, 1f)),
            MathF.Sqrt(Math.Clamp(rgb[2] * scale, 0f, 1f))).PackedValue;

    /// <summary>
    /// The animated conversion's hair + highlight colors as they will bake: the character's
    /// live colors (squared — colorset colors live in the squared domain) unless the override
    /// toggle is set; stored values also serve as the fallback while the character is
    /// unreadable. The effect color is always the stored one and not part of this.
    /// </summary>
    private (float[] Base, float[] Highlight) EffectiveAnimatedColors(AnimatedHairEdit edit)
    {
        if (edit.OverrideHairColors || LiveHair() is not { } live)
            return (edit.BaseColor, edit.HighlightColor);

        return ([live.Main.X * live.Main.X, live.Main.Y * live.Main.Y, live.Main.Z * live.Main.Z],
            [live.Highlight.X * live.Highlight.X, live.Highlight.Y * live.Highlight.Y, live.Highlight.Z * live.Highlight.Z]);
    }

    private (Vector3 Main, Vector3 Highlight)? HairPreviewColors(DTexture dTexture, MaterialKind kind)
    {
        if (kind is not MaterialKind.Hair)
            return null;

        var (main, highlight) = HairPreviewColorsPacked(dTexture, kind);
        var mainPacked        = new Rgba32(main);
        var highlightPacked   = new Rgba32(highlight);
        return (new Vector3(mainPacked.R / 255f, mainPacked.G / 255f, mainPacked.B / 255f),
            new Vector3(highlightPacked.R / 255f, highlightPacked.G / 255f, highlightPacked.B / 255f));
    }

    /// <summary>
    /// Extra viewport meshes rendered alongside the primary selected material. Body: overlay
    /// parts (nails, accents), each mesh routed through <see cref="ModelUvReader.GetBodyMesh"/>
    /// exactly like the companion bake so the editable geometry matches. Hair: the OTHER added
    /// hair materials of the same hair model — modded styles split their strands across several
    /// materials, and without these the viewport shows only part of the hairstyle.
    /// </summary>
    private List<ViewportOverlay> BuildOverlayEntries(DTexture dTexture, DecalLayer? placementLayer, string? boundPath,
        out int versionHash)
    {
        var hash   = 0;
        var result = new List<ViewportOverlay>();

        CompositePreviewCache.Entry EntryFor(string gamePath)
        {
            var entry = previewCache.Get(dTexture, gamePath,
                string.Equals(boundPath, gamePath, StringComparison.OrdinalIgnoreCase) ? placementLayer : null);
            hash = HashCode.Combine(hash, entry.Version);
            return entry;
        }

        if (_overlayOptions is { Count: > 0 } && ModelUvReader.IsBodySkinMaterial(_selectedMaterial))
            foreach (var option in _overlayOptions.Where(o => o.Slot is TextureSlot.Diffuse))
            {
                var source = dTexture.Data.Source.Materials.FirstOrDefault(m
                    => string.Equals(m.GamePath, option.MaterialGamePath, StringComparison.OrdinalIgnoreCase));
                var mesh = source == null ? null : uvReader.GetMesh(source);
                if (mesh == null)
                    continue;

                result.Add(new ViewportOverlay(mesh, PreviewBuffer(EntryFor(option.GamePath)), option.Kind is MaterialKind.Skin));
            }

        if (SelectedKind() is MaterialKind.Hair)
            AddHairSiblingEntries(dTexture, result, EntryFor);

        versionHash = hash;
        return result;
    }

    /// <summary> Hair sibling overlay entries: the other added hair materials sharing the primary's model. </summary>
    private void AddHairSiblingEntries(DTexture dTexture, List<ViewportOverlay> result,
        Func<string, CompositePreviewCache.Entry> entryFor)
    {
        var primary = FindMaterialSource(dTexture);
        if (primary == null)
            return;

        var hairColors = HairPreviewColors(dTexture, MaterialKind.Hair);
        // Companion hair materials live in the overlay list (hidden from the material
        // selector); older saves may still carry them as regular sources — scan both.
        foreach (var option in _options!.Concat(_overlayOptions ?? []))
        {
            if (option.Kind is not MaterialKind.Hair || option.Slot is not TextureSlot.Normal
             || string.Equals(option.MaterialGamePath, _selectedMaterial, StringComparison.OrdinalIgnoreCase))
                continue;

            var source = dTexture.Data.Source.Materials.FirstOrDefault(m
                => string.Equals(m.GamePath, option.MaterialGamePath, StringComparison.OrdinalIgnoreCase));
            if (source == null || !string.Equals(source.MdlGamePath, primary.MdlGamePath, StringComparison.OrdinalIgnoreCase))
                continue;

            var mesh = uvReader.GetMesh(source);
            if (mesh == null)
                continue;

            var maskPath = _options!.Find(o
                => string.Equals(o.MaterialGamePath, option.MaterialGamePath, StringComparison.OrdinalIgnoreCase)
                 && o.Slot is TextureSlot.Mask)?.GamePath;
            result.Add(new ViewportOverlay(mesh, PreviewBuffer(entryFor(option.GamePath)), false, hairColors,
                maskPath == null ? null : PreviewBuffer(entryFor(maskPath))));
        }
    }

    #endregion

    #region Texture preview

    private string               _previewTexturePath = string.Empty; // a texture game path, or a "gen:" pseudo-path
    private bool                 _previewRawView;
    private float                _previewZoom   = 1f;
    private Vector2              _previewCenter = new(0.5f, 0.5f);
    private IDalamudTextureWrap? _texturePreviewWrap;
    private (string Path, int Version, bool Source, bool Colors, uint HairMain, uint HairHighlight) _texturePreviewKey =
        (string.Empty, -1, false, false, 0u, 0u);

    // Generated companion previews of the animated conversion — what the build ACTUALLY
    // ships for converted hair, derived from the same composited buffers the build uses.
    private static readonly string[] GeneratedIds    = ["gen:norm", "gen:id", "gen:mask", "gen:fx"];
    private static readonly string[] GeneratedLabels = ["Converted Normal", "Generated ID Map", "Converted Mask", "Effect Pattern"];

    private (string Material, int NormalVersion, int MaskVersion, int Pattern, bool FullCoverage) _generatedKey =
        (string.Empty, -1, -1, -1, false);
    private readonly IDalamudTextureWrap?[] _generatedWraps = new IDalamudTextureWrap?[4];
    private readonly (int W, int H)[]       _generatedSizes = new (int, int)[4];

    private void DisposeGeneratedWraps()
    {
        for (var i = 0; i < _generatedWraps.Length; ++i)
        {
            _generatedWraps[i]?.Dispose();
            _generatedWraps[i] = null;
        }
    }

    /// <summary>
    /// The active material's textures as a clickable thumbnail strip plus one zoomable main
    /// view, always fed from the same composited preview-cache entries the viewport and the
    /// build use. With an enabled animated conversion the strip gains the GENERATED
    /// companions (converted normal, id map, mask, effect pattern) — the files the build
    /// actually ships, which look nothing like the source hair textures.
    /// </summary>
    private void DrawTexturePreview(DTexture dTexture)
    {
        var options = MaterialOptions();
        if (options.Count == 0)
            return;

        var animatedEdit = SelectedKind() is MaterialKind.Hair
         && dTexture.Data.AnimatedHair.GetValueOrDefault(_selectedMaterial) is { Enabled: true } a
            ? a
            : null;

        // Body-family companion canvases (face, nails, accents) are painted automatically by
        // the body's own layers — show their textures alongside the body's so the user can
        // check the continuation without selecting anything. The face also receives relief
        // and finish, so all its slots list; overlay parts only take diffuse decals.
        var companionOptions = _overlayOptions is { Count: > 0 } && ModelUvReader.IsBodySkinMaterial(_selectedMaterial)
            ? _overlayOptions.Where(o => ModelUvReader.IsFaceSkinMaterial(o.MaterialGamePath)
                ? o.Slot is TextureSlot.Diffuse or TextureSlot.Normal or TextureSlot.Mask
                : o.Slot is TextureSlot.Diffuse).ToList()
            : [];

        var generatedIndex = animatedEdit != null ? Array.IndexOf(GeneratedIds, _previewTexturePath) : -1;
        var current = generatedIndex >= 0
            ? null
            : options.Concat(companionOptions)
                 .FirstOrDefault(o => string.Equals(o.GamePath, _previewTexturePath, StringComparison.OrdinalIgnoreCase))
             ?? DefaultTargetOption() ?? options[0];

        // --- thumbnail strip: the material's textures, then the generated companions.
        void SelectPreview(string id)
        {
            _previewTexturePath = id;
            _previewZoom        = 1f;
            _previewCenter      = new Vector2(0.5f, 0.5f);
        }

        void Thumbnail(string id, string label, IDalamudTextureWrap? wrap, int width, int height, bool selected)
        {
            var thumbH = 44f * Im.Style.GlobalScale;
            var thumbW = Math.Clamp(height > 0 ? thumbH * width / height : thumbH, thumbH, thumbH * 2.2f);
            using var pushed = Im.Id.Push(id);
            var pos  = Im.Cursor.ScreenPosition;
            var size = new Vector2(thumbW, thumbH);
            if (Im.InvisibleButton("##thumb"u8, size))
                SelectPreview(id);

            var draw = Im.Window.DrawList;
            draw.Shape.RectangleFilled(pos, pos + size, 0xFF181818u);
            if (wrap != null)
                draw.Image(wrap.Id, pos, pos + size);
            draw.Shape.Rectangle(pos, pos + size, selected ? 0xFF53D7FFu : 0x40FFFFFFu, 0f, ImDrawFlagsRectangle.None,
                selected ? 2f : 1f);
            if (Im.Item.Hovered())
                Im.Tooltip.OnHover(HoveredFlags.None, $"{label}{(wrap == null ? " (loading...)" : $"  {width}x{height}")}");
            Im.Line.Same(0, 4f * Im.Style.GlobalScale);
        }

        foreach (var option in options)
        {
            var entry = previewCache.Get(dTexture, option.GamePath, null);
            Thumbnail(option.GamePath, $"{SlotButtonLabel(option)}\n{option.GamePath}",
                entry.CompositedWrap ?? entry.PristineWrap, entry.Pristine?.Width ?? 0, entry.Pristine?.Height ?? 0,
                current != null && ReferenceEquals(option, current));
        }

        foreach (var option in companionOptions)
        {
            var entry = previewCache.Get(dTexture, option.GamePath, null);
            Thumbnail(option.GamePath, $"{option.MaterialLabel} {SlotButtonLabel(option)} (painted by this canvas's layers)\n{option.GamePath}",
                entry.CompositedWrap ?? entry.PristineWrap, entry.Pristine?.Width ?? 0, entry.Pristine?.Height ?? 0,
                current != null && ReferenceEquals(option, current));
        }

        if (animatedEdit != null)
        {
            EnsureGeneratedPreviews(dTexture, animatedEdit);
            Im.Text("|"u8);
            Im.Tooltip.OnHover("Right of the bar: the GENERATED files the animated conversion ships — these replace the source textures in game."u8);
            Im.Line.Same(0, 4f * Im.Style.GlobalScale);
            for (var i = 0; i < GeneratedIds.Length; ++i)
            {
                if (_generatedWraps[i] == null)
                    continue;

                Thumbnail(GeneratedIds[i], $"{GeneratedLabels[i]} (generated by the Animated Effect build)",
                    _generatedWraps[i], _generatedSizes[i].W, _generatedSizes[i].H, generatedIndex == i);
            }
        }

        Im.Line.New();

        // --- header row of the main view: label, compare, hair-color view, zoom state.
        if (generatedIndex >= 0)
        {
            DrawGeneratedMainView(generatedIndex);
            return;
        }

        var entryMain = previewCache.Get(dTexture, current!.GamePath, null);
        var pristine  = entryMain.Pristine;
        if (pristine == null)
        {
            Im.Text("(loading texture...)"u8);
            return;
        }

        Im.Text($"{SlotButtonLabel(current)}  {pristine.Width}x{pristine.Height}");

        var showSource = false;
        if (entryMain.Composited != null)
        {
            Im.Line.Same();
            Im.Button("Hold: Source"u8);
            showSource = Im.Item.Active;
            Im.Tooltip.OnHover("Hold to see the untouched source texture — release to return to your edited version. Flipping back and forth makes the changes pop."u8);
        }

        // Hair normals are unreadable as raw data — default to rendering them as the two
        // hair colors blended by the highlight channel, exactly like the 3D preview does.
        var colorView = current is { Kind: MaterialKind.Hair, Slot: TextureSlot.Normal } && !_previewRawView;
        if (current is { Kind: MaterialKind.Hair, Slot: TextureSlot.Normal })
        {
            Im.Line.Same();
            if (Im.SmallButton(colorView ? "View: Hair Colors"u8 : "View: Raw Texture"u8))
                _previewRawView = !_previewRawView;
            Im.Tooltip.OnHover(
                "Hair Colors renders the highlight channel as your preview hair/highlight colors — what the hair will actually look like.\nRaw Texture shows the normal map data itself."u8);
        }

        DrawZoomIndicator();

        var rgba = !showSource && entryMain.Composited != null ? entryMain.Composited : pristine.Rgba;
        var (hairMain, hairHighlight) = colorView ? HairPreviewColorsPacked(dTexture, MaterialKind.Hair) : (0u, 0u);
        var key = (current.GamePath, entryMain.Version, showSource, colorView, hairMain, hairHighlight);
        if (_texturePreviewWrap == null || key != _texturePreviewKey)
        {
            if (colorView)
            {
                var mainPacked      = new Rgba32(hairMain);
                var highlightPacked = new Rgba32(hairHighlight);
                var main = new Vector3(mainPacked.R / 255f, mainPacked.G / 255f, mainPacked.B / 255f);
                var high = new Vector3(highlightPacked.R / 255f, highlightPacked.G / 255f, highlightPacked.B / 255f);
                main *= main; // squared RGB, like the shader
                high *= high;
                var colored = new byte[rgba.Length];
                for (var i = 0; i + 3 < rgba.Length; i += 4)
                {
                    var blend = rgba[i + 2] / 255f;
                    var color = Vector3.Lerp(main, high, blend) * 255f;
                    colored[i]     = (byte)Math.Clamp((int)color.X, 0, 255);
                    colored[i + 1] = (byte)Math.Clamp((int)color.Y, 0, 255);
                    colored[i + 2] = (byte)Math.Clamp((int)color.Z, 0, 255);
                    colored[i + 3] = rgba[i + 3]; // the card cutout stays visible
                }

                rgba = colored;
            }

            _texturePreviewWrap?.Dispose();
            _texturePreviewWrap = textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(pristine.Width, pristine.Height),
                rgba, "DTM Texture Preview");
            _texturePreviewKey = key;
        }

        DrawZoomableImage(_texturePreviewWrap, pristine.Width, pristine.Height, current.GamePath);
    }

    private void DrawGeneratedMainView(int index)
    {
        var wrap = _generatedWraps[index];
        if (wrap == null)
        {
            Im.Text("(generating...)"u8);
            return;
        }

        Im.Text($"{GeneratedLabels[index]}  {_generatedSizes[index].W}x{_generatedSizes[index].H}  (as built)");
        DrawZoomIndicator();
        DrawZoomableImage(wrap, _generatedSizes[index].W, _generatedSizes[index].H,
            "Generated by the Animated Effect conversion — this file ships in the built mod.");
    }

    private void DrawZoomIndicator()
    {
        if (_previewZoom <= 1.01f)
            return;

        Im.Line.Same();
        Im.Text($"{_previewZoom:F1}x");
        Im.Line.Same();
        if (Im.SmallButton("Reset Zoom"u8))
        {
            _previewZoom   = 1f;
            _previewCenter = new Vector2(0.5f, 0.5f);
        }
    }

    /// <summary>
    /// The main texture view: wheel zooms at the cursor, left-drag pans while zoomed,
    /// double-click resets — the zoom window is carried in UV space so any wrap works.
    /// </summary>
    private void DrawZoomableImage(IDalamudTextureWrap wrap, int texWidth, int texHeight, string tooltip)
    {
        var avail = Im.ContentRegion.Available;
        var maxH  = MathF.Max(180f * Im.Style.GlobalScale, avail.Y * 0.45f);
        var scale = MathF.Min(MathF.Max(avail.X, 1f) / texWidth, maxH / texHeight);
        var size  = new Vector2(texWidth * scale, texHeight * scale);

        var span = 1f / _previewZoom;
        _previewCenter = new Vector2(
            Math.Clamp(_previewCenter.X, span / 2f, 1f - span / 2f),
            Math.Clamp(_previewCenter.Y, span / 2f, 1f - span / 2f));
        var uv0 = _previewCenter - new Vector2(span / 2f);
        var uv1 = _previewCenter + new Vector2(span / 2f);

        var pos = Im.Cursor.ScreenPosition;
        Im.InvisibleButton("##texZoom"u8, size);
        Im.Window.DrawList.Image(wrap.Id, pos, pos + size, uv0, uv1);

        if (Im.Item.Hovered())
        {
            var wheel = Im.Io.MouseWheel;
            if (wheel != 0f)
            {
                var mouseFrac    = (Im.Mouse.Position - pos) / size;
                var uvUnderMouse = uv0 + mouseFrac * (uv1 - uv0);
                _previewZoom = Math.Clamp(_previewZoom * (1f + wheel * 0.2f), 1f, 16f);
                var newSpan = 1f / _previewZoom;
                _previewCenter = uvUnderMouse - (mouseFrac - new Vector2(0.5f, 0.5f)) * newSpan;
            }

            if (Im.Mouse.IsDoubleClicked(MouseButton.Left))
            {
                _previewZoom   = 1f;
                _previewCenter = new Vector2(0.5f, 0.5f);
            }

            Im.Tooltip.OnHover(HoveredFlags.None, $"{tooltip}\nWheel: zoom at the cursor.  Drag: pan while zoomed.  Double-click: reset.");
        }

        // Per-frame pan delta: the drag delta since the last reset stands in for the raw
        // per-frame mouse delta (the invisible button is only active with the button held).
        if (Im.Item.Active && _previewZoom > 1f)
        {
            var delta = Im.Mouse.GetDragDelta(MouseButton.Left, 0f);
            if (delta != Vector2.Zero)
            {
                _previewCenter -= delta / size * (uv1 - uv0);
                Im.Mouse.ResetDragDelta(MouseButton.Left);
            }
        }
    }

    /// <summary>
    /// (Re)build the generated-companion preview wraps when their inputs changed: the
    /// converted normal (cutout moved into B), the id map (highlight routing, or full
    /// coverage for tails), the character-family mask and the effect pattern — each the
    /// exact transform the build applies to the same composited buffers.
    /// </summary>
    private void EnsureGeneratedPreviews(DTexture dTexture, AnimatedHairEdit animated)
    {
        var normalOption = NormalOption();
        var maskOption   = MaterialOptions().Find(o => o.Slot is TextureSlot.Mask);
        var normalEntry  = normalOption == null ? null : previewCache.Get(dTexture, normalOption.GamePath, null);
        var maskEntry    = maskOption == null ? null : previewCache.Get(dTexture, maskOption.GamePath, null);
        if (normalEntry?.Pristine == null)
            return;

        var fullCoverage = EffectFullCoverage(normalEntry);
        var key = (_selectedMaterial, normalEntry.Version, maskEntry?.Version ?? -1,
            HashCode.Combine(animated.Pattern, animated.EffectLibraryId), fullCoverage);
        if (key == _generatedKey)
            return;

        _generatedKey = key;
        DisposeGeneratedWraps();

        var normalRgba = normalEntry.Composited ?? normalEntry.Pristine.Rgba;
        var nw = normalEntry.Pristine.Width;
        var nh = normalEntry.Pristine.Height;
        _generatedSizes[0] = (nw, nh);
        _generatedWraps[0] = textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(nw, nh),
            AnimatedHairBuilder.BuildNormalRgba(normalRgba), "DTM Gen Normal");
        _generatedSizes[1] = (nw, nh);
        _generatedWraps[1] = textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(nw, nh),
            AnimatedHairBuilder.BuildIdRgba(normalRgba, fullCoverage), "DTM Gen Id");

        if (maskEntry?.Pristine != null)
        {
            var maskRgba = maskEntry.Composited ?? maskEntry.Pristine.Rgba;
            _generatedSizes[2] = (maskEntry.Pristine.Width, maskEntry.Pristine.Height);
            _generatedWraps[2] = textureProvider.CreateFromRaw(
                RawImageSpecification.Rgba32(maskEntry.Pristine.Width, maskEntry.Pristine.Height),
                AnimatedHairBuilder.BuildCharMaskRgba(maskRgba), "DTM Gen Mask");
        }

        var (patternPixels, patternSize) = EffectPatternPixels(animated);
        _generatedSizes[3] = (patternSize, patternSize);
        _generatedWraps[3] = textureProvider.CreateFromRaw(
            RawImageSpecification.Rgba32(patternSize, patternSize), patternPixels, "DTM Gen Pattern");
    }

    /// <summary> Short slot label for the texture thumbnails; hair renames the channels to what they do. </summary>
    private static string SlotButtonLabel(TextureOption option)
        => option.Slot switch
        {
            TextureSlot.Diffuse  => "Color",
            TextureSlot.Normal   => option.Kind is MaterialKind.Hair ? "Highlights (Normal)" : "Normal",
            TextureSlot.Mask     => option.Kind is MaterialKind.Hair ? "Shine (Mask)" : "Mask",
            TextureSlot.Index    => "ID Map",
            TextureSlot.Specular => "Specular",
            _                    => "Other",
        };

    #endregion

    #region Hair adjustments

    /// <summary>
    /// Global hair adjustments for hair materials: highlight distribution (noise, gradient,
    /// contrast on the normal map's highlight-blend channel) and shine (mask channel scales).
    /// Each is a singleton layer at the BOTTOM of its texture's stack so decals stamp on top;
    /// the layer is only created once the user actually changes something — all-neutral
    /// settings never add build work.
    /// </summary>
    private void DrawHairSection(DTexture dTexture)
    {
        if (SelectedKind() is not MaterialKind.Hair)
            return;

        Im.Separator();
        if (!Im.Tree.Header("Hair Adjustments"u8, TreeNodeFlags.DefaultOpen))
            return;

        using var indent = Im.Indent();

        var normalOption = NormalOption();
        var maskOption   = MaterialOptions().Find(o => o.Slot is TextureSlot.Mask);
        if (maskOption != null)
            DrawShineControls(dTexture, maskOption);

        if (normalOption != null && _selectedMaterial.Length > 0)
        {
            Im.Separator();
            DrawAnimatedControls(dTexture);
        }

        if (normalOption == null && maskOption == null)
            Im.Text("This hair material exposes no normal or mask texture to adjust."u8);
    }

    /// <summary>
    /// All hair materials of the selected hairstyle — the selected one plus every other added
    /// hair material sharing its model. Modded styles split their strands across several
    /// materials; converting only one leaves the rest on the plain hair shader, so the
    /// animated conversion always applies to the whole set.
    /// </summary>
    private List<string> HairstyleMaterialPaths(DTexture dTexture)
    {
        var result  = new List<string> { _selectedMaterial };
        var primary = FindMaterialSource(dTexture);
        if (primary == null)
            return result;

        // Every source material of the same hair model — the auto-added hidden companions
        // included (they are not part of the material selector's options).
        foreach (var material in dTexture.Data.Source.Materials)
        {
            if (!result.Contains(material.GamePath, StringComparer.OrdinalIgnoreCase)
             && string.Equals(material.MdlGamePath, primary.MdlGamePath, StringComparison.OrdinalIgnoreCase))
                result.Add(material.GamePath);
        }

        return result;
    }

    private (string Material, int Count) _animatedMaterialCount = (string.Empty, 0);

    /// <summary>
    /// How many hair materials the selected hairstyle's MODEL references (the build converts
    /// them all). Cached per material — the count comes from parsing the model file.
    /// </summary>
    private int HairstyleModelMaterialCount(DTexture dTexture)
    {
        if (string.Equals(_animatedMaterialCount.Material, _selectedMaterial, StringComparison.OrdinalIgnoreCase))
            return _animatedMaterialCount.Count;

        var primary = FindMaterialSource(dTexture);
        var count = primary == null
            ? 1
            : Math.Max(1, uvReader.ModelMaterialNames(primary)
                .Count(n => AnimatedHairBuilder.IsHairMaterialName(System.IO.Path.GetFileName(n))));
        _animatedMaterialCount = (_selectedMaterial, count);
        return count;
    }

    /// <summary> Store the animated config on every material of the hairstyle and save. </summary>
    private void CommitAnimated(DTexture dTexture, AnimatedHairEdit staged)
    {
        foreach (var path in HairstyleMaterialPaths(dTexture))
            dTexture.Data.AnimatedHair[path] = string.Equals(path, _selectedMaterial, StringComparison.OrdinalIgnoreCase)
                ? staged
                : staged.Clone();

        Save(dTexture);
    }

    /// <summary>
    /// Animated-highlight conversion: swaps the hair material to the game's scrolling-effect
    /// shader so the highlight areas (authored + everything painted/edited into normal B)
    /// become an animated emissive effect. Colors and animation parameters bake into the
    /// replacement material's colorset and constants at build time. Applied to every material
    /// of the hairstyle at once (multi-material styles must convert together).
    /// </summary>
    private void DrawAnimatedControls(DTexture dTexture)
    {
        var edit    = dTexture.Data.AnimatedHair.GetValueOrDefault(_selectedMaterial);
        var staged  = edit ?? new AnimatedHairEdit();
        var changed = false;

        var enabled = staged.Enabled;
        if (Im.Checkbox("Animated Effect"u8, ref enabled))
        {
            staged.Enabled = enabled;
            changed        = true;
        }

        Im.Tooltip.OnHover(
            "Replaces this hairstyle's materials with the game's scrolling-effect shader: the highlight areas become a glowing effect that moves through the hair. Works whether or not your character's Highlights toggle is on — the areas come from the hair texture itself.\nApplies to every material of the hairstyle at once; the preview animates a stand-in of the effect (the exact look and speed need a Build to judge in game).\nThe hair and highlight colors follow your character unless overridden below; the effect color is the glow and is always picked here. Of the Shine sliders above, roughness and ambient occlusion carry into converted hair — specular and subsurface do not apply to it."u8);

        var hairstyleMaterials = HairstyleMaterialPaths(dTexture);

        // Heal older saves (and newly added sibling materials): an enabled conversion always
        // covers the whole hairstyle, so mirror it to any material that lacks it.
        if (staged.Enabled && !changed
         && hairstyleMaterials.Any(p => dTexture.Data.AnimatedHair.GetValueOrDefault(p) is not { Enabled: true }))
            CommitAnimated(dTexture, staged);

        if (staged.Enabled && HairstyleModelMaterialCount(dTexture) is > 1 and var modelMaterials)
        {
            Im.Line.Same();
            Im.Text($"({modelMaterials} materials)");
            Im.Tooltip.OnHover("This hairstyle's model splits across several hair materials — the build converts all of them together, whether or not each was added as a source."u8);
        }

        if (!staged.Enabled)
        {
            if (changed)
                CommitAnimated(dTexture, staged);

            return;
        }

        using var indent = Im.Indent();

        // Three colors: the effect (emissive glow) is always authored here; the hair and
        // highlight colors follow the character until the override toggle is set.
        var effectColor = new Vector3(staged.EffectColor[0], staged.EffectColor[1], staged.EffectColor[2]);
        if (Im.Color.Editor("Effect Color"u8, ref effectColor))
        {
            staged.EffectColor = [effectColor.X, effectColor.Y, effectColor.Z];
            changed            = true;
        }

        Im.Tooltip.OnHover("Emissive color of the moving effect — what glows and scrolls through the highlight areas."u8);

        var overrideColors = staged.OverrideHairColors;
        if (Im.Checkbox("Override Hair Colors"u8, ref overrideColors))
        {
            // Seed the overrides from what is currently shown so enabling starts from the
            // character's colors instead of jumping to a stale stored value.
            if (overrideColors)
            {
                var (liveBase, liveHighlight) = EffectiveAnimatedColors(staged);
                staged.BaseColor      = (float[])liveBase.Clone();
                staged.HighlightColor = (float[])liveHighlight.Clone();
            }

            staged.OverrideHairColors = overrideColors;
            changed                   = true;
        }

        Im.Tooltip.OnHover("The hair and highlight colors normally follow your character (Glamourer included) at every Build.\nEnable to pick both manually instead — the in-game hair color picker cannot reach converted hair."u8);

        if (staged.OverrideHairColors)
        {
            using var colorIndent = Im.Indent();

            var baseColor = new Vector3(staged.BaseColor[0], staged.BaseColor[1], staged.BaseColor[2]);
            if (Im.Color.Editor("Hair Color"u8, ref baseColor))
            {
                staged.BaseColor = [baseColor.X, baseColor.Y, baseColor.Z];
                changed          = true;
            }

            Im.Tooltip.OnHover("Baked color of the hair outside the highlight areas."u8);

            var highlightColor = new Vector3(staged.HighlightColor[0], staged.HighlightColor[1], staged.HighlightColor[2]);
            if (Im.Color.Editor("Highlight Color"u8, ref highlightColor))
            {
                staged.HighlightColor = [highlightColor.X, highlightColor.Y, highlightColor.Z];
                changed               = true;
            }

            Im.Tooltip.OnHover("Baked hair color of the highlight areas underneath the glowing effect."u8);
        }

        Im.Item.SetNextWidthScaled(220);
        var intensity = staged.EffectIntensity;
        if (Im.Slider("Effect Intensity"u8, ref intensity, "%.2f"u8, 0f, 4f))
        {
            staged.EffectIntensity = Math.Clamp(intensity, 0f, 4f);
            changed                = true;
        }

        Im.Tooltip.OnHover("Brightness of the glow — above 1 overdrives the effect color."u8);

        Im.Item.SetNextWidthScaled(150);
        var scrollU = staged.ScrollU;
        if (Im.Slider("##scrollU"u8, ref scrollU, "Across: %.2f"u8, -1f, 1f))
        {
            staged.ScrollU = scrollU;
            changed        = true;
        }

        Im.Tooltip.OnHover("Sideways drift of the pattern. Negative reverses, 0 freezes."u8);

        Im.Line.Same();
        Im.Item.SetNextWidthScaled(150);
        var scrollV = staged.ScrollV;
        if (Im.Slider("Scroll Speed"u8, ref scrollV, "Along: %.2f"u8, -1f, 1f))
        {
            staged.ScrollV = scrollV;
            changed        = true;
        }

        Im.Tooltip.OnHover("Drift along the strands. Negative reverses, 0 freezes."u8);

        Im.Item.SetNextWidthScaled(150);
        var tilingU = staged.TilingU;
        if (Im.Slider("##tilingU"u8, ref tilingU, "Across: %.2f"u8, 0.05f, 8f))
        {
            staged.TilingU = Math.Clamp(tilingU, 0.01f, 16f);
            changed        = true;
        }

        Im.Tooltip.OnHover("Pattern repeats across the strands — low = broad, high = fine."u8);

        Im.Line.Same();
        Im.Item.SetNextWidthScaled(150);
        var tilingV = staged.TilingV;
        if (Im.Slider("Pattern Tiling"u8, ref tilingV, "Along: %.2f"u8, 0.05f, 8f))
        {
            staged.TilingV = Math.Clamp(tilingV, 0.01f, 16f);
            changed        = true;
        }

        Im.Tooltip.OnHover("Pattern repeats along the strands — low = broad, high = fine."u8);

        var pattern    = (AnimatedHairBuilder.HairEffectPattern)staged.Pattern;
        var libraryEntry = staged.EffectLibraryId != Guid.Empty ? decals.GetEffect(staged.EffectLibraryId) : null;
        var comboLabel = staged.EffectLibraryId != Guid.Empty
            ? libraryEntry?.Name ?? "(missing library pattern)"
            : AnimatedHairBuilder.PatternLabel(pattern);
        Im.Item.SetNextWidthScaled(220);
        using (var combo = Im.Combo.Begin("Effect Pattern"u8, comboLabel))
        {
            if (combo)
            {
                foreach (var candidate in AnimatedHairBuilder.HairEffectPattern.Values)
                {
                    var active = staged.EffectLibraryId == Guid.Empty && candidate == pattern;
                    if (!Im.Selectable(AnimatedHairBuilder.PatternLabel(candidate), active) || active)
                        continue;

                    staged.Pattern         = (int)candidate;
                    staged.EffectLibraryId = Guid.Empty;
                    changed                = true;
                }

                if (decals.Effects.Count > 0)
                {
                    Im.Separator();
                    foreach (var (idx, effect) in decals.Effects.Index())
                    {
                        using var id     = Im.Id.Push(idx);
                        var       active = staged.EffectLibraryId == effect.Id;
                        if (!Im.Selectable($"Library: {effect.Name}", active) || active)
                            continue;

                        staged.EffectLibraryId = effect.Id;
                        changed                = true;
                    }
                }
            }
        }

        Im.Tooltip.OnHover(
            "The black/white pattern scrolled across the hair — bright areas show the effect color. Shown below as it tiles.\nGlint mimics the original reference (occasional single sparkles); Glitter loads the game's own hand-authored sparkle texture from your game files.\nLibrary entries are imported images, managed alongside the decals in the library window."u8);

        Im.Line.Same();
        if (Im.SmallButton("Manage Patterns..."u8))
            decalLibraryWindow.OpenEffects();

        Im.Tooltip.OnHover(
            "Open the Resource Library's Effect Patterns tab — import, rename or delete patterns there.\nImported patterns appear in this dropdown as \"Library:\" entries."u8);

        DrawPatternThumbnail(staged);

        if (!changed)
            return;

        CommitAnimated(dTexture, staged);
    }

    private (int Pattern, Guid LibraryId, IDalamudTextureWrap? Wrap) _patternThumbnail = (-1, Guid.Empty, null);

    /// <summary> A square preview of the active effect pattern (built-in, game texture or library import), rendered once per selection. </summary>
    private void DrawPatternThumbnail(AnimatedHairEdit edit)
    {
        if (_patternThumbnail.Pattern != edit.Pattern || _patternThumbnail.LibraryId != edit.EffectLibraryId)
        {
            _patternThumbnail.Wrap?.Dispose();
            var (pixels, size) = EffectPatternPixels(edit);
            _patternThumbnail = (edit.Pattern, edit.EffectLibraryId,
                textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(size, size), pixels));
        }

        // The pattern is always square — draw it square, or it reads stretched.
        if (_patternThumbnail.Wrap is { } wrap)
            Im.Image.Draw(wrap.Id, new Vector2(128, 128) * Im.Style.GlobalScale);
    }

    /// <summary> Find the singleton hair layer of a texture's stack, or stage a fresh neutral one. </summary>
    private static T HairLayerFor<T>(DTexture dTexture, TextureOption option, out bool exists) where T : TextureLayer, new()
    {
        var layer = dTexture.Data.Textures.GetValueOrDefault(option.GamePath)?.OfType<T>().FirstOrDefault();
        exists = layer != null;
        return layer ?? new T();
    }

    /// <summary> Attach a staged hair layer at the bottom of its texture's stack, capturing the pristine source. </summary>
    private void InsertHairLayer(DTexture dTexture, TextureOption option, TextureLayer layer)
    {
        if (!dTexture.Data.Textures.TryGetValue(option.GamePath, out var layers))
        {
            layers                                  = [];
            dTexture.Data.Textures[option.GamePath] = layers;
        }

        CaptureTextureSource(dTexture, option.GamePath);
        layers.Insert(0, layer);
    }

    /// <summary> Remove a hair singleton layer and drop its stack when that leaves it empty. </summary>
    private void RemoveHairLayer<T>(DTexture dTexture, TextureOption option) where T : TextureLayer
    {
        if (!dTexture.Data.Textures.TryGetValue(option.GamePath, out var layers))
            return;

        if (layers.RemoveAll(l => l is T) == 0)
            return;

        if (layers.Count == 0)
        {
            dTexture.Data.Textures.Remove(option.GamePath);
            dTexture.Data.TextureSourcePaths.Remove(option.GamePath);
        }

        Save(dTexture);
    }

    private void DrawShineControls(DTexture dTexture, TextureOption option)
    {
        var layer   = HairLayerFor<HairShineLayer>(dTexture, option, out var exists);
        var changed = false;

        Im.TextWrapped("Shine — how the hair surface responds to light."u8);

        if (Im.SmallButton("Glossy"u8))
        {
            layer.SpecScale       = 1.5f;
            layer.RoughnessScale  = 0.6f;
            layer.RoughnessOffset = -0.1f;
            changed               = true;
        }

        Im.Tooltip.OnHover("Sleek, reflective hair — boosted specular, lowered roughness."u8);
        Im.Line.Same();
        if (Im.SmallButton("Matte"u8))
        {
            layer.SpecScale       = 0.6f;
            layer.RoughnessScale  = 1.4f;
            layer.RoughnessOffset = 0.15f;
            changed               = true;
        }

        Im.Tooltip.OnHover("Dry, diffuse hair — dimmed specular, raised roughness."u8);

        Im.Item.SetNextWidthScaled(220);
        var spec = layer.SpecScale;
        if (Im.Slider("Specular"u8, ref spec, "×%.2f"u8, 0f, 2f))
        {
            layer.SpecScale = Math.Clamp(spec, 0f, 2f);
            changed         = true;
        }

        Im.Tooltip.OnHover("Multiplier on the authored specular power — below 1 dims reflections, above 1 boosts them."u8);

        Im.Item.SetNextWidthScaled(220);
        var roughnessScale = layer.RoughnessScale;
        if (Im.Slider("Roughness"u8, ref roughnessScale, "×%.2f"u8, 0f, 2f))
        {
            layer.RoughnessScale = Math.Clamp(roughnessScale, 0f, 2f);
            changed              = true;
        }

        Im.Item.SetNextWidthScaled(220);
        var roughnessOffset = layer.RoughnessOffset;
        if (Im.Slider("Roughness Offset"u8, ref roughnessOffset, "%+.2f"u8, -1f, 1f))
        {
            layer.RoughnessOffset = Math.Clamp(roughnessOffset, -1f, 1f);
            changed               = true;
        }

        Im.Tooltip.OnHover("Roughness spreads the shine out; the channel semantics are empirical — nudge and check in-game."u8);

        Im.Item.SetNextWidthScaled(220);
        var sss = layer.SssScale;
        if (Im.Slider("Subsurface"u8, ref sss, "×%.2f"u8, 0f, 2f))
        {
            layer.SssScale = Math.Clamp(sss, 0f, 2f);
            changed        = true;
        }

        Im.Tooltip.OnHover("Subsurface-scattering thickness — how much light glows through the strands."u8);

        Im.Item.SetNextWidthScaled(220);
        var ao = layer.AoScale;
        if (Im.Slider("Ambient Occlusion"u8, ref ao, "×%.2f"u8, 0f, 2f))
        {
            layer.AoScale = Math.Clamp(ao, 0f, 2f);
            changed       = true;
        }

        Im.Tooltip.OnHover("Multiplier on the authored shading darkness between strands."u8);

        if (exists)
        {
            if (Im.SmallButton("Reset Shine"u8))
            {
                RemoveHairLayer<HairShineLayer>(dTexture, option);
                return;
            }

            Im.Tooltip.OnHover("Remove the shine adjustment — the authored surface returns on the next build."u8);
        }

        if (!changed)
            return;

        if (!exists)
            InsertHairLayer(dTexture, option, layer);
        Save(dTexture);
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

        Im.Separator();
        if (!Im.Tree.Header("Manage Colorset"u8))
            return;

        using var indent = Im.Indent();

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
        Im.TextWrapped($"Analyzing id map: {sourceLabel}");
        if (capturedPath is { Length: > 0 } && Im.Item.Hovered())
            Im.Tooltip.OnHover(capturedPath);
        Im.Line.Same();
        if (Im.SmallButton("Reload Source"u8))
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

        Im.Tooltip.OnHover(
            "Drop the stored source capture and resolve the id map again from the currently active mods.\nUse this when the analyzed file is not the one your mod actually ships (e.g. the capture predates enabling or updating the source mod)."u8);

        EnsureIdStats(dTexture, option.GamePath);
        if (_sortedRowUsage.Count == 0)
        {
            Im.Text("No id-map statistics available for this texture."u8);
            return;
        }

        var claimedRows = ClaimedRowsForMaterial(dTexture, option.MaterialGamePath, null);
        // The base (no-edit) row colors depend only on the captured material — resolve once
        // per capture, not every frame this section is open.
        if (!ReferenceEquals(_manageRowDiffuseMtrl, option.Mtrl))
        {
            _manageRowDiffuseMtrl = option.Mtrl;
            _manageRowDiffuse     = MaterialEditApplier.ResolveRowDiffuse(option.Mtrl, null);
        }

        var rowDiffuse = _manageRowDiffuse;

        DrawSlotAvailability(dTexture, option, claimedRows, rowDiffuse);

        Im.Separator();
        Im.Text("Extract Baked Decal"u8);
        Im.Tooltip.OnHover(
            "Lift a decal that is already baked into this id map (e.g. by the source mod) out into a decal layer of its own: hover the eye of each row to see where it renders on your character, pick the row(s) the baked decal uses, then extract. The decal is moved onto free colorset slots of its own — the original texels are filled with the surrounding garment — so it can be recolored and repositioned without touching the rest of the gear."u8);

        _extractRows.RemoveWhere(claimedRows.Contains);

        // Small regions first — a baked decal is usually a small fraction of the garment.
        foreach (var (row, count) in _sortedRowUsage)
        {
            using var id      = Im.Id.Push(row);
            var       claimed = claimedRows.Contains(row);
            var       picked  = _extractRows.Contains(row);
            using (Im.Disabled(claimed))
            {
                if (Im.Checkbox($"Row {RowName(row)}", ref picked))
                {
                    if (picked)
                        _extractRows.Add(row);
                    else
                        _extractRows.Remove(row);
                }
            }

            Im.Line.Same();
            var color = rowDiffuse == null ? Vector3.One : rowDiffuse[row];
            Im.Color.Button("##rowColor"u8,
                new Vector4(Math.Clamp(color.X, 0f, 1f), Math.Clamp(color.Y, 0f, 1f), Math.Clamp(color.Z, 0f, 1f), 1f));
            Im.Line.Same();
            Im.Text($"{count} texels ({100f * count / _statsTotalTexels:F1}%){(claimed ? "  — claimed by a decal layer" : string.Empty)}");
            Im.Line.Same();
            DrawRowHighlightEye(option, row,
                "Highlights where this row dominantly renders on the character while hovered (redraws your character).\nA baked decal usually lives on a row the garment itself barely uses — often a slot's B half."u8);
        }

        Im.Checkbox("Largest Connected Region Only"u8, ref _extractLargestOnly);
        Im.Tooltip.OnHover(
            "Keep only the biggest connected patch of the selected rows.\nUseful when a row also covers unrelated texels elsewhere (a B half additionally catches the garment's deepest baked shading) — but turn it OFF if the decal itself is smaller than those other patches."u8);

        if (Im.Button("Extract Selected Rows as Decal"u8) && _extractRows.Count > 0)
            ExtractDecal(dTexture, option, table);
        if (_extractRows.Count == 0)
            Im.Tooltip.OnHover("Select at least one row above first."u8);

        if (_extractStatus.Length > 0)
            Im.TextWrapped(_extractStatus);
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
        Im.Text("Slot Availability"u8);
        Im.Tooltip.OnHover(
            "Which colorset slots decals may claim. Free slots are used automatically; slots the id map references are blocked — but a slot referenced by only a handful of stray texels is often fine to hand over with the Usable checkbox."u8);

        var edit = dTexture.Data.Materials.GetValueOrDefault(option.MaterialGamePath);
        for (var pair = 1; pair <= ColorRowAllocator.PairCount; ++pair)
        {
            using var id   = Im.Id.Push(pair);
            var       rowA = (pair - 1) * 2;

            var color = rowDiffuse == null ? Vector3.One : rowDiffuse[rowA];
            Im.Color.Button("##slotColor"u8,
                new Vector4(Math.Clamp(color.X, 0f, 1f), Math.Clamp(color.Y, 0f, 1f), Math.Clamp(color.Z, 0f, 1f), 1f));
            Im.Line.Same();
            Im.Text($"Slot {pair,2}");
            Im.Line.Same();

            if (claimedRows.Contains(rowA) || claimedRows.Contains(rowA + 1))
            {
                Im.Text("— claimed by a decal"u8);
                continue;
            }

            if (!_usedRowPairs.Contains(pair))
            {
                Im.Text("— free"u8);
                continue;
            }

            var usable = edit?.UsableSlots.Contains(pair) ?? false;
            if (Im.Checkbox("Usable"u8, ref usable))
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

            Im.Tooltip.OnHover(
                "Let decals claim this slot even though the id map references it.\nUse when the scanner is wrong (a few stray texels) or to sacrifice the slot deliberately — decals will overwrite its rows wherever the map really renders them."u8);

            Im.Line.Same();
            var texels = _rowUsageCounts.GetValueOrDefault(rowA) + _rowUsageCounts.GetValueOrDefault(rowA + 1);
            Im.Text($"— used by the map, {texels} texels ({100f * texels / _statsTotalTexels:F1}%)");
        }
    }

    /// <summary> Copy an extracted layer's temp stamp into the library — the explicit opt-in step. </summary>
    private void AddExtractedToLibrary(DTexture dTexture, TextureOption option, DecalLayer decal)
    {
        try
        {
            using var image = Image.Load<Rgba32>(decals.LayerImagePath(decal));
            var entry = decals.ImportGenerated(image, $"{option.MaterialLabel} — extracted decal");
            if (entry == null)
                return;

            decal.LibraryCopyId = entry.Id;
            Save(dTexture);
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Could not add the extracted decal to the library:\n{ex}");
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

        // The stamp is a temp file owned by this dTexture, NOT a library entry — re-running
        // the extraction must never pile up duplicates in the library. "Add to Library" on
        // the layer is the explicit step that keeps it for reuse.
        var stampFile = $"{dTexture.Identifier:N}_stamp_{Guid.NewGuid():N}.png";
        try
        {
            Directory.CreateDirectory(filenames.ExtractedDirectory);
            using var stamp = extraction.Stamp;
            stamp.SaveAsPng(Path.Combine(filenames.ExtractedDirectory, stampFile));
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Could not save the extracted stamp image:\n{ex}");
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
            LocalImageFile      = stampFile,
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
            $"Extracted {extraction.Rows.Count} row(s) into a decal layer ({extraction.W}x{extraction.H} texels), "
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
            TextureCompositor.EraseExtractedFootprint(image, layer, decals.LayerImagePath(layer));

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
