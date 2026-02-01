using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using DynamicTextureManager.DTextures;
using OtterGui.Text;

namespace DynamicTextureManager.UI;

public class DTMPanel: IDisposable
{
    private readonly DTMFileSystemSelector _selector;
    private readonly DTextureManager _manager;
    private readonly Configuration _config;
    private readonly HeaderDrawer.Button[] _leftButtons;
    private readonly HeaderDrawer.Button[] _rightButtons;
    
    public DTMPanel(DTMFileSystemSelector selector, DTextureManager manager, Configuration config)
    {
        _selector = selector;
        _manager  = manager;
        _config = config;
        _leftButtons =
        [
            
        ];
        _rightButtons =
        [
            
        ];
    }

    public void Dispose()
    {
        
    }
    
    public void Draw()
    {
        using var group = ImUtf8.Group();
        DrawHeader();
        DrawPanel();

        if (_selector.Selected == null || _selector.Selected.WriteProtected())
            return;
    }
    
    private void DrawHeader()
        => HeaderDrawer.Draw(SelectionName, 0, ImGui.GetColorU32(ImGuiCol.FrameBg), _leftButtons, _rightButtons);
    
    private string SelectionName
        => _selector.Selected == null ? "No Selection" : _selector.Selected.Name.Text;
    
    private void DrawPanel()
    {
        using var table = ImUtf8.Table("##Panel", 1, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.ScrollY, ImGui.GetContentRegionAvail());
        if (!table || _selector.Selected == null)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableNextColumn();
        if (_selector.Selected == null)
            return;

        ImGui.Dummy(Vector2.Zero);
        DrawButtonRow();
        ImGui.TableNextColumn();
    }
    
    private void DrawButtonRow()
    {
        DrawReload();
        ImGui.SameLine();
    }
    
    private void DrawReload()
    {
        //
    }
}