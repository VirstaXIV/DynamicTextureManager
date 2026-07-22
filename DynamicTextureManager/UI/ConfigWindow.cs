using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DynamicTextureManager.Interop;
using DynamicTextureManager.ModGeneration;
using DynamicTextureManager.Services;
using OtterGui.Extensions;
using OtterGui.Raii;
using OtterGui.Services;
using OtterGui.Text;

namespace DynamicTextureManager.UI;

public class ConfigWindowPosition : IService
{
    public bool IsOpen { get; set; }
    public Vector2 Position { get; set; }
    public Vector2 Size { get; set; }
}

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration _configuration;
    private readonly ConfigWindowPosition _position;
    private readonly PenumbraService _penumbra;
    private readonly OverlayModManager _overlayMods;
    private readonly DecalLibrary _decals;

    private readonly Dalamud.Interface.ImGuiFileDialog.FileDialogManager _fileDialog = new();

    private string  _decalFolderInput = string.Empty;
    private bool    _decalFolderInputInitialized;
    private string? _decalFolderStatus;
    private bool    _decalFolderStatusIsError;

    public ConfigWindow(Configuration configuration, ConfigWindowPosition position, PenumbraService penumbra, OverlayModManager overlayMods,
        DecalLibrary decals)
        : base("Dynamic Texture Manager: Configuration")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(700, 600)
        };

        _configuration = configuration;
        _position = position;
        _penumbra = penumbra;
        _overlayMods = overlayMods;
        _decals = decals;
    }

    public void Dispose() { }

    public override void Draw()
    {
        _position.Size = ImGui.GetWindowSize();
        _position.Position = ImGui.GetWindowPos();
        _fileDialog.Draw();

        Checkbox("Auto Rebuild"u8, "Automatically rebuild the generated mod shortly after edits, once it has been built with the hammer button."u8,
            _configuration.AutoReload, v => _configuration.AutoReload = v);

        Checkbox("Delete Mod With dTexture"u8, "When a dTexture is deleted, also delete its generated mod from Penumbra."u8,
            _configuration.DeleteModWithDTexture, v => _configuration.DeleteModWithDTexture = v);

        var maxColors = _configuration.DefaultDecalMaxColors;
        ImGui.SetNextItemWidth(150 * ImUtf8.GlobalScale);
        if (ImUtf8.Slider("##defaultMaxColors"u8, ref maxColors, "%d"u8, 2, 12))
        {
            _configuration.DefaultDecalMaxColors = maxColors;
            _configuration.Save();
        }

        ImGui.SameLine();
        ImUtf8.LabeledHelpMarker("Default Decal Colors"u8,
            "How many colors newly added colorset decals extract from their image at most.\nEach color claims one free colorset row; the cap can be changed per decal afterwards."u8);

        ImGui.Spacing();
        DrawDecalStorage();
        if (_configuration.DebugMode)
        {
            ImGui.Spacing();
            DrawMaskDebug();
        }

        ImGui.Spacing();
        DrawPenumbraStatus();
        ImGui.Spacing();
        DrawOrphanedMods();
    }

    private void DrawDecalStorage()
    {
        ImGui.Separator();
        ImUtf8.Text("Decal Storage"u8);
        ImUtf8.HoverTooltip("Where imported decal images are kept. The library index always stays in the plugin config directory.\nApplying a new folder copies all decal images there and removes the old copies."u8);

        if (!_decalFolderInputInitialized)
        {
            _decalFolderInput            = _configuration.DecalStorageFolder;
            _decalFolderInputInitialized = true;
        }

        ImGui.SetNextItemWidth(400 * ImUtf8.GlobalScale);
        ImUtf8.InputText("##decalFolder"u8, ref _decalFolderInput, "(default: plugin config directory)"u8);
        ImUtf8.HoverTooltip($"Current folder: {_decals.StorageDirectory}");

        ImGui.SameLine();
        if (ImUtf8.SmallButton("Browse..."u8))
            _fileDialog.OpenFolderDialog("Select Decal Storage Folder", (success, path) =>
            {
                if (!success)
                    return;

                _decalFolderInput = path;
                SetFolderStatus(_decals.MoveStorage(path));
            }, _decals.StorageDirectory);
        ImUtf8.HoverTooltip("Pick a folder — the decal images are moved there right away."u8);

        ImGui.SameLine();
        if (ImUtf8.SmallButton("Apply"u8))
            SetFolderStatus(_decals.MoveStorage(_decalFolderInput));

        ImGui.SameLine();
        if (ImUtf8.SmallButton("Reset to Default"u8))
        {
            SetFolderStatus(_decals.MoveStorage(string.Empty));
            if (!_decalFolderStatusIsError)
                _decalFolderInput = string.Empty;
        }

        if (_decalFolderStatus != null)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, _decalFolderStatusIsError ? 0xFF00A0FFu : 0xFF40C040u);
            ImUtf8.TextWrapped(_decalFolderStatus);
        }
    }

    private void SetFolderStatus(string? error)
    {
        _decalFolderStatusIsError = error != null;
        _decalFolderStatus        = error ?? $"Saved — decals are stored in {_decals.StorageDirectory}.";
    }

    /// <summary>
    /// Dawntrail mask channel semantics are still empirical; these knobs let an in-game
    /// verification session retarget the finish write without a plugin rebuild.
    /// </summary>
    private void DrawMaskDebug()
    {
        ImGui.Separator();
        ImUtf8.Text("Mask Finish Debug"u8);
        ImUtf8.HoverTooltip("Empirical mask-map channel semantics for the decal surface finish.\nOnly change these while verifying finish behavior in-game; rebuild the mod after changing them."u8);

        var channel = _configuration.MaskRoughnessChannel;
        ImGui.SetNextItemWidth(150 * ImUtf8.GlobalScale);
        if (ImUtf8.Slider("Roughness Channel (0=R 1=G 2=B)"u8, ref channel, "%d"u8, 0, 2))
        {
            _configuration.MaskRoughnessChannel = channel;
            _configuration.Save();
            FinishMapping.Sync(_configuration);
        }

        Checkbox("Invert Roughness"u8, "Set if the mask channel stores gloss (1 - roughness) instead of roughness."u8,
            _configuration.MaskInvertRoughness, v =>
            {
                _configuration.MaskInvertRoughness = v;
                FinishMapping.Sync(_configuration);
            });

        Checkbox("Write Specular Channel"u8, "Also scale the mask's R channel by the finish's specular multiplier."u8,
            _configuration.MaskWriteSpec, v =>
            {
                _configuration.MaskWriteSpec = v;
                FinishMapping.Sync(_configuration);
            });
    }

    private void DrawOrphanedMods()
    {
        if (!_penumbra.Available)
            return;

        var orphans = _overlayMods.GetOrphanedMods();
        if (orphans.Count == 0)
            return;

        ImGui.Separator();
        ImUtf8.Text("Orphaned Generated Mods"u8);
        ImUtf8.HoverTooltip("Mods generated by this plugin that no dTexture references anymore.\nThey keep working in Penumbra, but the plugin can no longer rebuild them."u8);

        foreach (var ((directory, name), idx) in orphans.WithIndex())
        {
            using var id = ImUtf8.PushId(idx);
            ImUtf8.TextWrapped($"{name} ({directory})");
            ImGui.SameLine();
            if (ImUtf8.SmallButton("Open"u8))
                _penumbra.OpenModInPenumbra(directory);
            ImGui.SameLine();
            if (ImUtf8.SmallButton("Delete"u8) && ImGui.GetIO().KeyCtrl)
                _overlayMods.DeleteOrphan(directory);
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("Hold Control and click to delete this mod from Penumbra permanently."u8);
        }
    }

    private void DrawPenumbraStatus()
    {
        ImGui.Separator();
        ImUtf8.Text("Penumbra"u8);
        if (!_penumbra.Available)
        {
            ImUtf8.Text("Not connected — is Penumbra installed and enabled?"u8);
            return;
        }

        ImUtf8.Text($"API Version: {_penumbra.Version.Breaking}.{_penumbra.Version.Features}");
        try
        {
            ImUtf8.TextWrapped($"Mod Directory: {_penumbra.GetModDirectory()}");
        }
        catch (Exception ex)
        {
            ImUtf8.Text($"Mod Directory unavailable: {ex.Message}");
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void Checkbox(ReadOnlySpan<byte> label, ReadOnlySpan<byte> tooltip, bool current, Action<bool> setter)
    {
        using var id = ImUtf8.PushId(label);
        var tmp = current;
        if (ImUtf8.Checkbox(""u8, ref tmp) && tmp != current)
        {
            setter(tmp);
            _configuration.Save();
        }

        ImGui.SameLine();
        ImUtf8.LabeledHelpMarker(label, tooltip);
    }
}
