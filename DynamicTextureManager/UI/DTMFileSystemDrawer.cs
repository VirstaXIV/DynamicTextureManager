using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.Events;
using DynamicTextureManager.ModGeneration;
using ImSharp;
using Luna;

namespace DynamicTextureManager.UI;

public sealed class DTMFileSystemDrawer : FileSystemDrawer<DTMFileSystemCache.DTextureNode>
{
    internal readonly OverlayModManager OverlayMods;
    internal readonly DTextureChanged   DTextureChanged;

    public DTMFileSystemDrawer(MessageService messager, DTextureFileSystem fileSystem, DTextureManager manager,
        Configuration config, OverlayModManager overlayMods, DTextureChanged dTextureChanged)
        : base(messager, fileSystem, new DTMFilter())
    {
        OverlayMods     = overlayMods;
        DTextureChanged = dTextureChanged;

        MainContext.AddButton(new GlobalSortModeSelector(this, m =>
        {
            config.SortMode = m;
            config.Save();
        }), -100);

        Footer.Buttons.AddButton(new NewDTextureButton(manager),                  1000);
        Footer.Buttons.AddButton(new CloneDTextureButton(fileSystem, manager),    900);
        Footer.Buttons.AddButton(new DeleteSelectionButton(fileSystem, manager, config), -100);

        SortMode = config.SortMode;
    }

    /// <summary> The single selected canvas group, if exactly one is selected. </summary>
    public DTexture? Selected
        => FileSystem.Selection.Selection?.GetValue<DTexture>();

    public override IEnumerable<ISortMode> ValidSortModes
        => Configuration.Constants.ValidSortModes;

    public override ReadOnlySpan<byte> Id
        => "CanvasGroups"u8;

    protected override FileSystemCache<DTMFileSystemCache.DTextureNode> CreateCache()
        => new DTMFileSystemCache(this);
}

/// <summary> The plain substring filter over full paths the old selector provided. </summary>
public sealed class DTMFilter : TextFilterBase<DTMFileSystemCache.DTextureNode>, IFileSystemFilter<DTMFileSystemCache.DTextureNode>
{
    protected override string ToFilterString(in DTMFileSystemCache.DTextureNode item, int _)
        => item.Node.FullPath;

    public bool WouldBeVisible(in FileSystemFolderCache folder)
        => IsEmpty || folder.FullPath.Contains(Text, Comparison);

    public override bool DrawFilter(ReadOnlySpan<byte> label, Vector2 availableRegion)
    {
        var ret = base.DrawFilter(label, availableRegion);
        Im.Tooltip.OnHover("Filter canvas groups for those where their full paths or names contain the given substring."u8);
        return ret;
    }
}

public sealed class NewDTextureButton(DTextureManager manager) : BaseIconButton<AwesomeIcon>
{
    public override AwesomeIcon Icon
        => LunaStyle.AddObjectIcon;

    public override bool HasTooltip
        => true;

    public override void DrawTooltip()
        => Im.Text("Create a new canvas group. Each group builds into one Penumbra mod."u8);

    public override void OnClick()
        => Im.Popup.Open("##NewDTexture"u8);

    protected override void PostDraw()
    {
        if (InputPopup.OpenName("##NewDTexture"u8, out var newName) && newName.Length > 0)
            manager.CreateEmpty(newName);
    }
}

public sealed class CloneDTextureButton(DTextureFileSystem fileSystem, DTextureManager manager) : BaseIconButton<AwesomeIcon>
{
    private DTexture? _clone;

    public override AwesomeIcon Icon
        => LunaStyle.DuplicateIcon;

    public override bool HasTooltip
        => true;

    public override bool Enabled
        => fileSystem.Selection.Selection is not null;

    public override void DrawTooltip()
    {
        if (fileSystem.Selection.Selection?.GetValue<DTexture>() is { } dTexture)
            Im.Text($"Clone {dTexture.Name}.");
        else
            Im.Text("No canvas group selected."u8);
    }

    public override void OnClick()
    {
        _clone = fileSystem.Selection.Selection?.GetValue<DTexture>();
        Im.Popup.Open("##CloneDTexture"u8);
    }

    protected override void PostDraw()
    {
        if (!InputPopup.OpenName("##CloneDTexture"u8, out var newName) || newName.Length == 0)
            return;

        if (_clone != null)
            manager.CreateClone(_clone, newName);
        _clone = null;
    }
}

public sealed class DeleteSelectionButton(DTextureFileSystem fileSystem, DTextureManager manager, Configuration config)
    : BaseIconButton<AwesomeIcon>
{
    public override AwesomeIcon Icon
        => LunaStyle.DeleteIcon;

    public override bool HasTooltip
        => true;

    public override bool Enabled
        => config.DeleteDTextureModifier.IsActive() && fileSystem.Selection.DataNodes.Count > 0;

    public override void DrawTooltip()
    {
        Im.Text(fileSystem.Selection.DataNodes.Count > 0
            ? "Delete the currently selected canvas groups entirely from your drive.\nThis can not be undone."u8
            : "No canvas groups selected."u8);
        if (!Enabled)
            Im.Text($"\nHold {config.DeleteDTextureModifier} while clicking to delete the canvas groups.");
    }

    public override void OnClick()
    {
        var dTextures = fileSystem.Selection.DataNodes.Select(n => n.Value).OfType<DTexture>().ToList();
        fileSystem.Selection.UnselectAll();
        foreach (var dTexture in dTextures)
            manager.Delete(dTexture);
    }
}

/// <summary> A menu selector for the global sort mode; local stand-in for the one in newer Luna versions. </summary>
public sealed class GlobalSortModeSelector(FileSystemDrawer drawer, Action<ISortMode>? configSetter) : BaseButton
{
    public override ReadOnlySpan<byte> Label
        => "Global Sort Mode"u8;

    public override bool DrawMenuItem()
    {
        LunaStyle.DrawSeparator();
        Im.Text("Global Sorting:"u8);
        if (!SortModeCombo.DrawCombo(drawer.ValidSortModes, "##sortCombo"u8, drawer.SortMode, out var newSortMode, false,
                180 * Im.Style.GlobalScale))
            return false;

        drawer.SortMode = newSortMode!;
        configSetter?.Invoke(newSortMode!);
        return true;
    }
}
