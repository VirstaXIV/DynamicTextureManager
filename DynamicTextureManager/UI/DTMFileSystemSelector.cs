using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures;
using OtterGui;
using OtterGui.Filesystem;
using OtterGui.FileSystem.Selector;
using OtterGui.Log;
using OtterGui.Text;

namespace DynamicTextureManager.UI;

public sealed class DTMFileSystemSelector : FileSystemSelector<DTexture, DTMFileSystemSelector.DTextureState>
{
    private readonly DTextureManager _dTextureManager;
    private readonly Configuration _config;
    
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
        Configuration config, Logger log)
        : base(fileSystem, keyState, log, allowMultipleSelection: true)
    {
        _dTextureManager = dTextureManager;
        _config = config;

        AddButton(NewDTextureButton, 0);
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
            _cloneDTexture   = null;
            ImGui.OpenPopup("##NewDTexture");
        }
    }
    
    private void DeleteButton(Vector2 size)
        => DeleteSelectionButton(size, _config.DeleteDTextureModifier, "dTexture", "dTextures", _dTextureManager.Delete);
    
    private void SetFilterTooltip()
    {
        FilterTooltip = "Filter dTextures for those where their full paths or names contain the given substring.";
    }
}