using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using DynamicTextureManager.Interop;
using DynamicTextureManager.ModGeneration;
using DynamicTextureManager.UI.Panels;
using OtterGui.Text;

namespace DynamicTextureManager.UI;

public class DTMPanel : IDisposable
{
    private readonly DTMFileSystemSelector _selector;
    private readonly OverlayModManager _overlayMods;
    private readonly PenumbraService _penumbra;
    private readonly EditPreviewer _previewer;
    private readonly SourceTab _sourceTab;
    private readonly DecalsTab _decalsTab;
    private readonly TextureViewerTab _textureViewerTab;
    private readonly HeaderDrawer.Button[] _leftButtons;
    private readonly HeaderDrawer.Button[] _rightButtons;

    public DTMPanel(DTMFileSystemSelector selector, OverlayModManager overlayMods, PenumbraService penumbra, EditPreviewer previewer,
        SourceTab sourceTab, DecalsTab decalsTab, TextureViewerTab textureViewerTab)
    {
        _selector         = selector;
        _overlayMods      = overlayMods;
        _penumbra         = penumbra;
        _previewer        = previewer;
        _sourceTab        = sourceTab;
        _decalsTab        = decalsTab;
        _textureViewerTab = textureViewerTab;
        _leftButtons      = [new ApplyButton(this)];
        _rightButtons     = [new DeleteModButton(this)];
    }

    private sealed class ApplyButton(DTMPanel panel) : HeaderDrawer.Button
    {
        protected override FontAwesomeIcon Icon
            => FontAwesomeIcon.Hammer;

        protected override string Description
            => "Build: bake the current edits into the generated Penumbra mod (and enable it).\nUse the \"Enabled\" checkbox below to toggle the mod on or off.";

        protected override bool Disabled
            => panel._selector.Selected == null || panel._overlayMods.Busy;

        protected override void OnClick()
            => panel.Apply();
    }

    private sealed class DeleteModButton(DTMPanel panel) : HeaderDrawer.Button
    {
        protected override FontAwesomeIcon Icon
            => FontAwesomeIcon.Trash;

        protected override string Description
            => "Delete the generated Penumbra mod of this dTexture (keeps the dTexture itself).";

        protected override bool Disabled
            => panel._selector.Selected == null || panel._selector.Selected.Data.OutputModDirectory.Length == 0;

        protected override void OnClick()
            => panel.DeleteMod();
    }

    public void Dispose()
    { }

    public void Draw()
    {
        using var group = ImUtf8.Group();
        DrawHeader();
        DrawPanel();
    }

    private void DrawHeader()
        => HeaderDrawer.Draw(SelectionName, 0, ImGui.GetColorU32(ImGuiCol.FrameBg), _leftButtons, _rightButtons);

    private string SelectionName
        => _selector.Selected == null ? "No Selection" : _selector.Selected.Name.Text;

    private void Apply()
    {
        if (_selector.Selected == null)
            return;

        // Remove the temporary preview first — if the look persists after Apply, the
        // persistent mod works; if it reverts, the persistent chain is what failed.
        _previewer.Clear();
        _overlayMods.Apply(_selector.Selected);
    }

    private void DeleteMod()
    {
        if (_selector.Selected != null)
            _overlayMods.DeleteMod(_selector.Selected);
    }

    private void DrawPanel()
    {
        using var child = ImUtf8.Child("##Panel"u8, ImGui.GetContentRegionAvail(), true);
        if (!child || _selector.Selected == null)
            return;

        var selected = _selector.Selected;

        if (_overlayMods.LastResult.Length > 0)
            ImUtf8.Text(_overlayMods.LastResult);
        DrawGeneratedModLine(selected);

        var decalsDrawn = false;
        using (var tabBar = ImUtf8.TabBar("##tabs"u8))
        {
            if (tabBar)
            {
                using (var tab = ImUtf8.TabItem("Source"u8))
                {
                    if (tab)
                        _sourceTab.Draw(selected);
                }

                using (var tab = ImUtf8.TabItem("Decals"u8))
                {
                    if (tab)
                    {
                        decalsDrawn = true;
                        _decalsTab.Draw(selected);
                    }
                }

                using (var tab = ImUtf8.TabItem("Textures"u8))
                {
                    if (tab)
                        _textureViewerTab.Draw(selected);
                }
            }
        }

        // The live preview must not linger while the user is not editing — a stale
        // temporary mod overrides everything and makes Penumbra toggles look broken.
        if (!decalsDrawn)
            _previewer.Clear();
    }

    private void DrawGeneratedModLine(DTextures.DTexture selected)
    {
        if (selected.Data.OutputModDirectory.Length == 0)
            return;

        ImUtf8.Text($"Generated Mod: {selected.Data.OutputModDirectory}");
        if (!_penumbra.Available)
            return;

        var state = _overlayMods.QueryModState(selected);
        if (state != null)
        {
            ImGui.SameLine();
            var enabled = state.Value.Enabled;
            if (ImUtf8.Checkbox("Enabled"u8, ref enabled))
                _overlayMods.SetModEnabled(selected, enabled);
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip($"Enable or disable the generated mod in collection \"{state.Value.CollectionName}\".");
        }

        ImGui.SameLine();
        if (ImUtf8.SmallButton("Open in Penumbra"u8))
            _penumbra.OpenModInPenumbra(selected.Data.OutputModDirectory);
        if (ImGui.IsItemHovered())
            ImUtf8.HoverTooltip("Open the generated mod in Penumbra's mod tab."u8);
    }
}
