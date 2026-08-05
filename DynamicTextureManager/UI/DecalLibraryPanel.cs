using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration;
using DynamicTextureManager.Services;
using OtterGui.Raii;
using OtterGui.Text;

namespace DynamicTextureManager.UI;

/// <summary>
/// The decal library browser: import, search, tag-filter and sort the shared decal images,
/// edit the selected entry's name/tags/preset. Used by the standalone library window in
/// manage mode and, with a pick callback, as the picker dialog the Decals tab opens.
/// </summary>
public sealed class DecalLibraryPanel(DecalLibrary decals, ITextureProvider textureProvider, TextureIO textureIO)
    : OtterGui.Services.IService, IDisposable
{
    private enum SortMode
    {
        DateDesc,
        DateAsc,
        NameAsc,
        NameDesc,
    }

    private readonly FileDialogManager _fileDialog = new();

    private string          _search    = string.Empty;
    private readonly HashSet<string> _tagFilter = new(StringComparer.OrdinalIgnoreCase);
    private SortMode        _sort      = SortMode.DateDesc;
    private Guid            _selected  = Guid.Empty;
    private string          _renameBuffer = string.Empty;
    private string          _tagBuffer    = string.Empty;

    /// <summary> Called by the picker flow after an in-panel import, so a fresh decal can be picked immediately. </summary>
    public DecalEntry? LastImported { get; private set; }

    // Filtering, sorting and tag aggregation over the whole library are cached and redone
    // only when the library or the filter inputs actually change, not per frame.
    private int      _viewRevision = -1;
    private int      _viewTagStamp = -1;
    private int      _tagStamp;
    private string   _viewSearch   = string.Empty;
    private SortMode _viewSort     = SortMode.DateDesc;

    private List<(DecalEntry Entry, string Path)> _viewEntries = [];
    private List<string>                          _viewTags    = [];

    private void EnsureView()
    {
        if (_viewRevision == decals.Revision && _viewTagStamp == _tagStamp
         && _viewSort == _sort && string.Equals(_viewSearch, _search, StringComparison.Ordinal))
            return;

        _viewRevision = decals.Revision;
        _viewTagStamp = _tagStamp;
        _viewSort     = _sort;
        _viewSearch   = _search;
        _viewEntries  = Filtered().Select(e => (e, decals.FilePath(e.Id))).ToList();
        _viewTags     = decals.AllTags();
    }

    public void Draw(Action<DecalEntry>? onPick = null)
    {
        _fileDialog.Draw();

        EnsureView();
        DrawTopBar();
        DrawTagFilter();
        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        var detailHeight = _selected != Guid.Empty ? 170 * ImUtf8.GlobalScale : 0;
        using (var grid = ImUtf8.Child("##decalGrid"u8, new Vector2(avail.X, avail.Y - detailHeight)))
        {
            if (grid)
                DrawGrid(onPick);
        }

        if (_selected != Guid.Empty)
            DrawSelectionDetails();
    }

    private void DrawTopBar()
    {
        if (ImUtf8.Button("Import Decal..."u8))
            _fileDialog.OpenFileDialog("Import Decal", "Images{.png,.jpg,.jpeg,.dds,.bmp,.tga}", (success, path) =>
            {
                if (!success)
                    return;

                LastImported = decals.Import(path);
                if (LastImported != null)
                    _selected = LastImported.Id;
            });
        ImUtf8.HoverTooltip("Import an image into the decal library. It is converted to PNG and can be stamped onto textures."u8);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200 * ImUtf8.GlobalScale);
        ImUtf8.InputText("##search"u8, ref _search, "Search..."u8);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150 * ImUtf8.GlobalScale);
        using var combo = ImUtf8.Combo("##sort"u8, SortLabel(_sort));
        if (combo)
            foreach (var mode in Enum.GetValues<SortMode>())
                if (ImUtf8.Selectable(SortLabel(mode), mode == _sort))
                    _sort = mode;
    }

    private void DrawTagFilter()
    {
        if (_viewTags.Count == 0)
            return;

        ImUtf8.Text("Tags:"u8);
        foreach (var (tag, idx) in _viewTags.Select((t, i) => (t, i)))
        {
            ImGui.SameLine();
            using var id     = ImUtf8.PushId(idx);
            var       active = _tagFilter.Contains(tag);
            using var color  = ImRaii.PushColor(ImGuiCol.Button, 0xFF885522u, active);
            if (ImUtf8.SmallButton(tag))
            {
                if (!_tagFilter.Add(tag))
                    _tagFilter.Remove(tag);
                ++_tagStamp;
            }
        }

        if (_tagFilter.Count > 0)
        {
            ImGui.SameLine();
            if (ImUtf8.SmallButton("Clear Filter"u8))
            {
                _tagFilter.Clear();
                ++_tagStamp;
            }
        }
    }

    private IEnumerable<DecalEntry> Filtered()
    {
        IEnumerable<DecalEntry> entries = decals.Decals;
        if (_search.Length > 0)
            entries = entries.Where(d => d.Name.Contains(_search, StringComparison.OrdinalIgnoreCase));
        if (_tagFilter.Count > 0)
            entries = entries.Where(d => _tagFilter.All(t => d.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));

        return _sort switch
        {
            SortMode.NameAsc  => entries.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase),
            SortMode.NameDesc => entries.OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase),
            SortMode.DateAsc  => entries.OrderBy(d => d.CreatedDate),
            _                 => entries.OrderByDescending(d => d.CreatedDate),
        };
    }

    private void DrawGrid(Action<DecalEntry>? onPick)
    {
        var entries = _viewEntries;
        if (entries.Count == 0)
        {
            ImUtf8.Text(decals.Decals.Count == 0 ? "No decals imported yet."u8 : "No decals match the current filter."u8);
            return;
        }

        var cellSize  = 96 * ImUtf8.GlobalScale;
        var spacing   = ImGui.GetStyle().ItemSpacing.X;
        var availX    = ImGui.GetContentRegionAvail().X;
        var perRow    = Math.Max(1, (int)((availX + spacing) / (cellSize + spacing)));

        foreach (var ((entry, path), idx) in entries.Select((e, i) => (e, i)))
        {
            if (idx % perRow != 0)
                ImGui.SameLine();

            using var id    = ImUtf8.PushId(idx);
            using var group = ImUtf8.Group();

            var wrap     = textureProvider.GetFromFile(path).GetWrapOrDefault();
            var selected = entry.Id == _selected;
            using (var border = ImRaii.PushColor(ImGuiCol.Button, 0xFF885522u, selected))
            {
                var clicked = wrap != null
                    ? ImGui.ImageButton(wrap.Handle, new Vector2(cellSize - 12 * ImUtf8.GlobalScale))
                    : ImUtf8.Button("?"u8, new Vector2(cellSize - 12 * ImUtf8.GlobalScale));
                if (clicked)
                {
                    if (onPick != null)
                    {
                        onPick(entry);
                    }
                    else
                    {
                        _selected     = entry.Id;
                        _renameBuffer = entry.Name;
                        _tagBuffer    = string.Empty;
                    }
                }
            }

            var label = entry.Name.Length > 14 ? entry.Name[..13] + "…" : entry.Name;
            ImUtf8.Text(label);

            group.Dispose();
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip(onPick != null
                    ? $"{entry.Name}\n{TagLine(entry)}Click to use this decal."
                    : $"{entry.Name}\n{TagLine(entry)}Click to select and edit.");
        }
    }

    private static string TagLine(DecalEntry entry)
        => entry.Tags.Count > 0 ? $"Tags: {string.Join(", ", entry.Tags)}\n" : string.Empty;

    private void DrawSelectionDetails()
    {
        var entry = decals.Get(_selected);
        if (entry == null)
        {
            _selected = Guid.Empty;
            return;
        }

        ImGui.Separator();
        var thumbSize = 128 * ImUtf8.GlobalScale;
        var wrap      = textureProvider.GetFromFile(decals.FilePath(entry.Id)).GetWrapOrDefault();
        if (wrap != null)
            ImGui.Image(wrap.Handle, new Vector2(thumbSize));
        else
            ImGui.Dummy(new Vector2(thumbSize));

        ImGui.SameLine();
        using var group = ImUtf8.Group();

        ImGui.SetNextItemWidth(250 * ImUtf8.GlobalScale);
        ImUtf8.InputText("##rename"u8, ref _renameBuffer);
        ImGui.SameLine();
        if (ImUtf8.SmallButton("Rename"u8) && _renameBuffer.Trim().Length > 0)
            decals.Rename(entry.Id, _renameBuffer.Trim());

        // Tag chips with removal, plus an input to add new ones.
        ImUtf8.Text("Tags:"u8);
        foreach (var (tag, idx) in entry.Tags.Select((t, i) => (t, i)))
        {
            ImGui.SameLine();
            using var id = ImUtf8.PushId(idx);
            if (ImUtf8.SmallButton($"{tag} ×"))
            {
                decals.SetTags(entry.Id, entry.Tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
                break;
            }

            ImUtf8.HoverTooltip("Click to remove this tag."u8);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * ImUtf8.GlobalScale);
        var addTag = ImUtf8.InputText("##addTag"u8, ref _tagBuffer, "add tag..."u8, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImUtf8.SmallButton("+"u8) || addTag) && _tagBuffer.Trim().Length > 0)
        {
            decals.SetTags(entry.Id, entry.Tags.Append(_tagBuffer.Trim()));
            _tagBuffer = string.Empty;
        }

        // Preset summary — presets are authored from a placed layer via "Save Settings to Library".
        if (entry.Preset is { } preset)
        {
            var finish = preset.Finish switch
            {
                DecalFinishMode.Matte  => "matte",
                DecalFinishMode.Glossy => "glossy",
                DecalFinishMode.Custom => $"custom finish (roughness {preset.FinishRoughness:F2})",
                _                      => "finish untouched",
            };
            var colors = preset.IdRemap ? $"{preset.MaxColors} colors" : "full color";
            ImUtf8.TextWrapped($"Preset: {colors}, {finish}, opacity {preset.Opacity:F2}");
            ImUtf8.HoverTooltip("Settings applied when this decal is attached to gear — saved from a placed decal with \"Save Settings to Library\"."u8);
            ImGui.SameLine();
            if (ImUtf8.SmallButton("Clear Preset"u8))
                decals.SetPreset(entry.Id, null);
        }
        else
        {
            ImUtf8.Text("No preset — attachments start from defaults."u8);
            ImUtf8.HoverTooltip("Attach the decal to gear, adjust its colors and finish, then use \"Save Settings to Library\" on the layer to store them here."u8);
        }

        ImUtf8.Text($"Added: {entry.CreatedDate.ToLocalTime():yyyy-MM-dd}");

        ImGui.SameLine();
        if (ImUtf8.SmallButton("Delete"u8) && ImGui.GetIO().KeyCtrl)
        {
            decals.Delete(entry.Id);
            _selected = Guid.Empty;
        }

        ImUtf8.HoverTooltip("Hold Control and click to delete this decal from the library.\nAlready-built mods keep working — they bake the pixels in — but layers referencing it can no longer rebuild."u8);
    }

    private static string SortLabel(SortMode mode)
        => mode switch
        {
            SortMode.DateAsc  => "Oldest First",
            SortMode.NameAsc  => "Name A-Z",
            SortMode.NameDesc => "Name Z-A",
            _                 => "Newest First",
        };

    #region Effect patterns

    private Guid   _selectedEffect = Guid.Empty;
    private string _effectRename   = string.Empty;

    private readonly Dictionary<int, Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap> _builtinWraps = [];
    private Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? _gameGlitterWrap;
    private bool _gameGlitterTried;

    public void Dispose()
    {
        foreach (var wrap in _builtinWraps.Values)
            wrap.Dispose();
        _builtinWraps.Clear();
        _gameGlitterWrap?.Dispose();
    }

    /// <summary>
    /// The effect-pattern side of the resource library: the patterns shipped with the plugin
    /// (procedurally generated), the ones read from the game's own files, and imported
    /// images — stored with the decals and deletable here.
    /// </summary>
    public void DrawEffects()
    {
        _fileDialog.Draw();

        if (ImUtf8.Button("Import Effect Pattern..."u8))
            _fileDialog.OpenFileDialog("Import Effect Pattern", "Images{.png,.jpg,.jpeg,.dds,.bmp,.tga}", (success, path) =>
            {
                if (!success)
                    return;

                var imported = decals.ImportEffect(path);
                if (imported != null)
                    _selectedEffect = imported.Id;
            });
        ImUtf8.HoverTooltip(
            "Import an image as a scrolling effect pattern for the Animated Effect. It is converted to PNG and stored with the decals.\nBrightness becomes the glow; the image should tile cleanly in both directions."u8);

        ImGui.Separator();
        ImUtf8.Text("Built into the plugin:"u8);
        var cell = 72f * ImUtf8.GlobalScale;
        foreach (var pattern in Enum.GetValues<ModGeneration.AnimatedHairBuilder.HairEffectPattern>())
        {
            if (pattern is ModGeneration.AnimatedHairBuilder.HairEffectPattern.DressGlitter)
                continue;

            using var id    = ImUtf8.PushId((int)pattern);
            using var group = ImUtf8.Group();
            if (!_builtinWraps.TryGetValue((int)pattern, out var wrap))
            {
                var size = ModGeneration.AnimatedHairBuilder.PatternDimension(pattern);
                _builtinWraps[(int)pattern] = wrap = textureProvider.CreateFromRaw(
                    Dalamud.Interface.Textures.RawImageSpecification.Rgba32(size, size),
                    ModGeneration.AnimatedHairBuilder.GeneratePattern(pattern, size), $"DTM Pattern {pattern}");
            }

            ImGui.Image(wrap.Handle, new Vector2(cell));
            ImUtf8.Text(ModGeneration.AnimatedHairBuilder.PatternLabel(pattern));
            group.Dispose();
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("Part of the plugin — always available."u8);
            ImGui.SameLine();
        }

        ImGui.NewLine();
        ImGui.Separator();
        ImUtf8.Text("From the game:"u8);
        if (!_gameGlitterTried)
        {
            _gameGlitterTried = true;
            var glitter = textureIO.Load(ModGeneration.AnimatedHairBuilder.DressGlitterTexPath, null, null);
            if (glitter != null)
                _gameGlitterWrap = textureProvider.CreateFromRaw(
                    Dalamud.Interface.Textures.RawImageSpecification.Rgba32(glitter.Width, glitter.Height), glitter.Rgba,
                    "DTM Pattern Glitter");
        }

        if (_gameGlitterWrap is { } game)
        {
            using var group = ImUtf8.Group();
            var aspect = game.Width / (float)game.Height;
            ImGui.Image(game.Handle, new Vector2(cell * aspect, cell));
            ImUtf8.Text("Glitter"u8);
            group.Dispose();
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("From the Neo Queen's Dress."u8);
        }
        else
        {
            ImUtf8.Text("(could not read the game texture)"u8);
        }

        ImGui.Separator();
        ImUtf8.Text("Imported:"u8);
        if (decals.Effects.Count == 0)
        {
            ImUtf8.Text("No effect patterns imported yet."u8);
            return;
        }

        foreach (var (effect, idx) in decals.Effects.Select((e, i) => (e, i)))
        {
            using var id    = ImUtf8.PushId(idx);
            using var group = ImUtf8.Group();
            var wrap     = textureProvider.GetFromFile(decals.EffectFilePath(effect.Id)).GetWrapOrDefault();
            var selected = effect.Id == _selectedEffect;
            using (ImRaii.PushColor(ImGuiCol.Button, 0xFF885522u, selected))
            {
                var clicked = wrap != null
                    ? ImGui.ImageButton(wrap.Handle, new Vector2(cell))
                    : ImUtf8.Button("?"u8, new Vector2(cell));
                if (clicked)
                {
                    _selectedEffect = effect.Id;
                    _effectRename   = effect.Name;
                }
            }

            var label = effect.Name.Length > 12 ? effect.Name[..11] + "…" : effect.Name;
            ImUtf8.Text(label);
            group.Dispose();
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip($"{effect.Name}\nImported {effect.CreatedDate.ToLocalTime():yyyy-MM-dd} from {effect.OriginalFile}\nClick to select.");
            ImGui.SameLine();
        }

        ImGui.NewLine();
        if (_selectedEffect == Guid.Empty || decals.GetEffect(_selectedEffect) is not { } selectedEntry)
            return;

        ImGui.Separator();
        ImGui.SetNextItemWidth(250 * ImUtf8.GlobalScale);
        ImUtf8.InputText("##effectRename"u8, ref _effectRename);
        ImGui.SameLine();
        if (ImUtf8.SmallButton("Rename"u8) && _effectRename.Trim().Length > 0)
            decals.RenameEffect(selectedEntry.Id, _effectRename.Trim());

        ImGui.SameLine();
        if (ImUtf8.SmallButton("Delete"u8) && ImGui.GetIO().KeyCtrl)
        {
            decals.DeleteEffect(selectedEntry.Id);
            _selectedEffect = Guid.Empty;
        }

        ImUtf8.HoverTooltip(
            "Hold Control and click to delete this pattern from the library.\nBuilt mods keep working — they bake the pattern in — but animated effects referencing it fall back to their built-in pattern on the next build."u8);
    }

    #endregion
}
