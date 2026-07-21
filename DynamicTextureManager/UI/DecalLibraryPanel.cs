using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Services;
using OtterGui.Raii;
using OtterGui.Text;

namespace DynamicTextureManager.UI;

/// <summary>
/// The decal library browser: import, search, tag-filter and sort the shared decal images,
/// edit the selected entry's name/tags/preset. Used by the standalone library window in
/// manage mode and, with a pick callback, as the picker dialog the Decals tab opens.
/// </summary>
public sealed class DecalLibraryPanel(DecalLibrary decals, ITextureProvider textureProvider) : OtterGui.Services.IService
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

    public void Draw(Action<DecalEntry>? onPick = null)
    {
        _fileDialog.Draw();

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
        var allTags = decals.AllTags();
        if (allTags.Count == 0)
            return;

        ImUtf8.Text("Tags:"u8);
        foreach (var (tag, idx) in allTags.Select((t, i) => (t, i)))
        {
            ImGui.SameLine();
            using var id     = ImUtf8.PushId(idx);
            var       active = _tagFilter.Contains(tag);
            using var color  = ImRaii.PushColor(ImGuiCol.Button, 0xFF885522u, active);
            if (ImUtf8.SmallButton(tag))
            {
                if (!_tagFilter.Add(tag))
                    _tagFilter.Remove(tag);
            }
        }

        if (_tagFilter.Count > 0)
        {
            ImGui.SameLine();
            if (ImUtf8.SmallButton("Clear Filter"u8))
                _tagFilter.Clear();
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
        var entries = Filtered().ToList();
        if (entries.Count == 0)
        {
            ImUtf8.Text(decals.Decals.Count == 0 ? "No decals imported yet."u8 : "No decals match the current filter."u8);
            return;
        }

        var cellSize  = 96 * ImUtf8.GlobalScale;
        var spacing   = ImGui.GetStyle().ItemSpacing.X;
        var availX    = ImGui.GetContentRegionAvail().X;
        var perRow    = Math.Max(1, (int)((availX + spacing) / (cellSize + spacing)));

        foreach (var (entry, idx) in entries.Select((e, i) => (e, i)))
        {
            if (idx % perRow != 0)
                ImGui.SameLine();

            using var id    = ImUtf8.PushId(idx);
            using var group = ImUtf8.Group();

            var wrap     = textureProvider.GetFromFile(decals.FilePath(entry.Id)).GetWrapOrDefault();
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
            ImUtf8.Text($"Preset: {colors}, {finish}, opacity {preset.Opacity:F2}");
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
}
