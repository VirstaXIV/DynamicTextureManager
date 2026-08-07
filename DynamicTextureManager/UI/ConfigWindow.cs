using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Interface.Windowing;
using DynamicTextureManager.Interop;
using DynamicTextureManager.ModGeneration;
using DynamicTextureManager.Services;
using ImSharp;
using Luna;
using Window = Dalamud.Interface.Windowing.Window;

namespace DynamicTextureManager.UI;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration _configuration;
    private readonly PenumbraService _penumbra;
    private readonly OverlayModManager _overlayMods;
    private readonly DecalLibrary _decals;

    private readonly Dalamud.Interface.ImGuiFileDialog.FileDialogManager _fileDialog = new();

    private string  _decalFolderInput = string.Empty;
    private bool    _decalFolderInputInitialized;
    private string? _decalFolderStatus;
    private bool    _decalFolderStatusIsError;

    public ConfigWindow(Configuration configuration, PenumbraService penumbra, OverlayModManager overlayMods,
        DecalLibrary decals)
        : base("Dynamic Texture Manager: Configuration")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(700, 600)
        };

        _configuration = configuration;
        _penumbra = penumbra;
        _overlayMods = overlayMods;
        _decals = decals;
    }

    public void Dispose() { }

    public override void Draw()
    {
        // The shared ImSharp context attaches on a framework tick after service
        // construction — Im.* calls before that dereference an empty context.
        if (!ImSharpConfiguration.IsInitialized)
            return;

        _fileDialog.Draw();

        Checkbox("Auto Rebuild"u8, "Automatically rebuild the generated mod shortly after edits, once it has been built with the hammer button."u8,
            _configuration.AutoReload, v => _configuration.AutoReload = v);

        Checkbox("Delete Mod With Canvas Group"u8, "When a canvas group is deleted, also delete its generated mod from Penumbra."u8,
            _configuration.DeleteModWithDTexture, v => _configuration.DeleteModWithDTexture = v);

        var maxColors = _configuration.DefaultDecalMaxColors;
        Im.Item.SetNextWidthScaled(150);
        if (Im.Slider("##defaultMaxColors"u8, ref maxColors, "%d"u8, 2, 12))
        {
            _configuration.DefaultDecalMaxColors = maxColors;
            _configuration.Save();
        }

        LunaStyle.DrawHelpMarkerLabel("Default Decal Colors"u8,
            "How many colors newly added colorset decals extract from their image at most.\nEach color claims one free colorset row; the cap can be changed per decal afterwards."u8);

        Im.Line.Spacing();
        DrawDecalStorage();
        if (_configuration.DebugMode)
        {
            Im.Line.Spacing();
            DrawMaskDebug();
        }

        Im.Line.Spacing();
        DrawPenumbraStatus();
        Im.Line.Spacing();
        DrawOrphanedMods();
    }

    private void DrawDecalStorage()
    {
        Im.Separator();
        Im.Text("Decal Storage"u8);
        Im.Tooltip.OnHover("Where imported decal images are kept. The library index always stays in the plugin config directory.\nApplying a new folder copies all decal images there and removes the old copies."u8);

        if (!_decalFolderInputInitialized)
        {
            _decalFolderInput            = _configuration.DecalStorageFolder;
            _decalFolderInputInitialized = true;
        }

        Im.Item.SetNextWidthScaled(400);
        Im.Input.Text("##decalFolder"u8, ref _decalFolderInput, "(default: plugin config directory)"u8);
        Im.Tooltip.OnHover(HoveredFlags.None, $"Current folder: {_decals.StorageDirectory}");

        Im.Line.Same();
        if (Im.SmallButton("Browse..."u8))
            _fileDialog.OpenFolderDialog("Select Decal Storage Folder", (success, path) =>
            {
                if (!success)
                    return;

                _decalFolderInput = path;
                SetFolderStatus(_decals.MoveStorage(path));
            }, _decals.StorageDirectory);
        Im.Tooltip.OnHover("Pick a folder — the decal images are moved there right away."u8);

        Im.Line.Same();
        if (Im.SmallButton("Apply"u8))
            SetFolderStatus(_decals.MoveStorage(_decalFolderInput));

        Im.Line.Same();
        if (Im.SmallButton("Reset to Default"u8))
        {
            SetFolderStatus(_decals.MoveStorage(string.Empty));
            if (!_decalFolderStatusIsError)
                _decalFolderInput = string.Empty;
        }

        if (_decalFolderStatus != null)
        {
            using var color = ImGuiColor.Text.Push(new Rgba32(_decalFolderStatusIsError ? 0xFF00A0FFu : 0xFF40C040u));
            Im.TextWrapped(_decalFolderStatus);
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
        Im.Separator();
        Im.Text("Mask Finish Debug"u8);
        Im.Tooltip.OnHover("Empirical mask-map channel semantics for the decal surface finish.\nOnly change these while verifying finish behavior in-game; rebuild the mod after changing them."u8);

        var channel = _configuration.MaskRoughnessChannel;
        Im.Item.SetNextWidthScaled(150);
        if (Im.Slider("Roughness Channel (0=R 1=G 2=B)"u8, ref channel, "%d"u8, 0, 2))
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

        Checkbox("Flip Procedural Normal G"u8, "Flip the generated relief's green channel if fur/scale bumps light from the wrong side in-game."u8,
            _configuration.ProceduralNormalFlipG, v =>
            {
                _configuration.ProceduralNormalFlipG = v;
                FinishMapping.Sync(_configuration);
            });

        Checkbox("Procedural Cavity to Mask R"u8, "Also darken the mask's R channel in procedural crevices (cavity/spec occlusion)."u8,
            _configuration.ProceduralMaskWriteCavity, v =>
            {
                _configuration.ProceduralMaskWriteCavity = v;
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

        Im.Separator();
        Im.Text("Orphaned Generated Mods"u8);
        Im.Tooltip.OnHover("Mods generated by this plugin that no canvas group references anymore.\nThey keep working in Penumbra, but the plugin can no longer rebuild them."u8);

        for (var idx = 0; idx < orphans.Count; ++idx)
        {
            var (directory, name) = orphans[idx];
            using var id = Im.Id.Push(idx);
            Im.TextWrapped($"{name} ({directory})");
            Im.Line.Same();
            if (Im.SmallButton("Open"u8))
                _penumbra.OpenModInPenumbra(directory);
            Im.Line.Same();
            if (Im.SmallButton("Delete"u8) && Im.Io.KeyControl)
                _overlayMods.DeleteOrphan(directory);
            Im.Tooltip.OnHover("Hold Control and click to delete this mod from Penumbra permanently."u8);
        }
    }

    private void DrawPenumbraStatus()
    {
        Im.Separator();
        Im.Text("Penumbra"u8);
        if (!_penumbra.Available)
        {
            Im.Text("Not connected — is Penumbra installed and enabled?"u8);
            return;
        }

        Im.Text($"API Version: {_penumbra.Version.Breaking}.{_penumbra.Version.Features}");
        try
        {
            Im.TextWrapped($"Mod Directory: {_penumbra.GetModDirectory()}");
        }
        catch (Exception ex)
        {
            Im.Text($"Mod Directory unavailable: {ex.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void Checkbox(ReadOnlySpan<byte> label, ReadOnlySpan<byte> tooltip, bool current, Action<bool> setter)
    {
        using var id = Im.Id.Push(label);
        var tmp = current;
        if (Im.Checkbox(""u8, ref tmp) && tmp != current)
        {
            setter(tmp);
            _configuration.Save();
        }

        LunaStyle.DrawHelpMarkerLabel(label, tooltip);
    }
}
