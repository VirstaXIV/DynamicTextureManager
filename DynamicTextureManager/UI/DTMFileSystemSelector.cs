using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.ModGeneration;
using OtterGui;
using OtterGui.Filesystem;
using OtterGui.FileSystem.Selector;
using OtterGui.Log;
using OtterGui.Raii;
using OtterGui.Text;

namespace DynamicTextureManager.UI;

public sealed class DTMFileSystemSelector : FileSystemSelector<DTexture, DTMFileSystemSelector.DTextureState>
{
    private readonly DTextureManager _dTextureManager;
    private readonly Configuration _config;
    private readonly OverlayModManager _overlayMods;

    private DTexture? _cloneDTexture;
    private string  _newName = string.Empty;
    
    public record struct DTextureState(uint Color)
    { }
    
    protected override float CurrentWidth
        => _config.CurrentDTextureSelectorWidth * ImUtf8.GlobalScale;
    
    protected override float MinimumScaling
        => _config.DTMSelectorMinimumScale;

    protected override float MaximumScaling
        => _config.DTMSelectorMaximumScale;
    
    public DTMFileSystemSelector(DTextureManager dTextureManager, DTextureFileSystem fileSystem, IKeyState keyState,
        Configuration config, Logger log, OverlayModManager overlayMods)
        : base(fileSystem, keyState, log, allowMultipleSelection: true)
    {
        _dTextureManager = dTextureManager;
        _config = config;
        _overlayMods = overlayMods;

        AddButton(NewDTextureButton, 0);
        AddButton(CloneDTextureButton, 10);
        AddButton(DeleteButton, 1000);
        UnsubscribeRightClickLeaf(RenameLeaf);
        SetFilterTooltip();
        
        if (_config.SelectedDTexture == Guid.Empty)
            return;

        var dTexture = dTextureManager.DTextures.ByIdentifier(_config.SelectedDTexture);
        if (dTexture != null)
            SelectByValue(dTexture);
    }
    
    public override void Dispose()
    {
        base.Dispose();
    }
    
    public override ISortMode<DTexture> SortMode
        => _config.SortMode;
    
    protected override bool FoldersDefaultOpen
        => _config.OpenFoldersByDefault;
    
    private void NewDTextureButton(Vector2 size)
    {
        if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Plus.ToIconString(), size, "Create a new dTexture with default configuration.", false,
                true))
        {
            _cloneDTexture = null;
            _newName       = string.Empty;
            ImGui.OpenPopup("##NewDTexture");
        }

        DrawNewDTexturePopup();
    }

    private void CloneDTextureButton(Vector2 size)
    {
        var tooltip = Selected == null ? "No dTexture selected." : $"Clone {Selected.Name}.";
        if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Clone.ToIconString(), size, tooltip, Selected == null, true))
        {
            _cloneDTexture = Selected;
            _newName       = string.Empty;
            ImGui.OpenPopup("##NewDTexture");
        }
    }

    private void DrawNewDTexturePopup()
    {
        if (!ImGuiUtil.OpenNameField("##NewDTexture", ref _newName) || _newName.Length == 0)
            return;

        if (_cloneDTexture != null)
            _dTextureManager.CreateClone(_cloneDTexture, _newName);
        else
            _dTextureManager.CreateEmpty(_newName);

        _cloneDTexture = null;
        _newName       = string.Empty;
    }


    private void DeleteButton(Vector2 size)
        => DeleteSelectionButton(size, _config.DeleteDTextureModifier, "dTexture", "dTextures", _dTextureManager.Delete);

    /// <summary> Gray out entries whose generated mod is currently disabled in Penumbra. </summary>
    protected override void DrawLeafName(FileSystem<DTexture>.Leaf leaf, in DTextureState state, bool selected)
    {
        var flag     = selected ? ImGuiTreeNodeFlags.Selected | LeafFlags : LeafFlags;
        var disabled = _overlayMods.IsModEnabled(leaf.Value) == false;

        using var color = ImRaii.PushColor(ImGuiCol.Text, ColorId.DisabledMod.Value(), disabled);
        using var _     = ImRaii.TreeNode(leaf.Name, flag);
        if (disabled && ImGui.IsItemHovered())
            ImUtf8.HoverTooltip("The generated mod of this dTexture is currently disabled in Penumbra."u8);
    }
    
    private void SetFilterTooltip()
    {
        FilterTooltip = "Filter dTextures for those where their full paths or names contain the given substring.";
    }
}