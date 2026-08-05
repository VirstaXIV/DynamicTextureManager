using System;
using System.Numerics;
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
    private readonly HeaderDrawer.Button[] _leftButtons;
    private readonly HeaderDrawer.Button[] _rightButtons;

    public DTMPanel(DTMFileSystemSelector selector, OverlayModManager overlayMods, PenumbraService penumbra, EditPreviewer previewer,
        SourceTab sourceTab, DecalsTab decalsTab)
    {
        _selector     = selector;
        _overlayMods  = overlayMods;
        _penumbra     = penumbra;
        _previewer    = previewer;
        _sourceTab    = sourceTab;
        _decalsTab    = decalsTab;
        _leftButtons  = [new ApplyButton(this)];
        _rightButtons = [new DeleteModButton(this)];
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
            => "Delete the generated Penumbra mod of this canvas group (keeps the canvas group itself).";

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

    /// <summary>
    /// One consolidated view instead of Source/Decals/Textures tabs: the left column holds a
    /// collapsible Source section (pick + set the active material) above the editing controls,
    /// the right column shows the active material's composited texture directly above the 3D
    /// preview — every edit is visible on both at once.
    /// </summary>
    private void DrawPanel()
    {
        using var child = ImUtf8.Child("##Panel"u8, ImGui.GetContentRegionAvail(), true);
        if (!child || _selector.Selected == null)
            return;

        var selected = _selector.Selected;

        if (_overlayMods.LastResult.Length > 0)
            ImUtf8.Text(_overlayMods.LastResult);
        DrawGeneratedModLine(selected);
        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        var leftWidth = MathF.Min(MathF.Max(avail.X * 0.48f, 380f * ImUtf8.GlobalScale),
            MathF.Max(avail.X - 340f * ImUtf8.GlobalScale, 260f * ImUtf8.GlobalScale));
        using (var left = ImUtf8.Child("##controls"u8, new Vector2(leftWidth, avail.Y), false))
        {
            if (left)
            {
                if (ImUtf8.CollapsingHeader("Canvases"u8,
                        selected.Data.Source.IsEmpty ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                {
                    _sourceTab.Draw(selected);
                    ImGui.Separator();
                }

                _decalsTab.DrawControls(selected);
            }
        }

        ImGui.SameLine();
        using (var right = ImUtf8.Child("##visuals"u8, ImGui.GetContentRegionAvail(), false))
        {
            if (right)
                _decalsTab.DrawVisuals(selected);
        }
    }

    private void DrawGeneratedModLine(DTextures.DTexture selected)
    {
        if (selected.Data.OutputModDirectory.Length == 0)
        {
            // Until the first explicit Build, slider changes only affect the preview — the
            // auto-apply path deliberately never creates the mod on its own.
            if (selected.Data.HasEdits)
                ImUtf8.TextWrapped(
                    "Not built yet — edits show only in the preview. Press the hammer button above to build the Penumbra mod and see them in-game; afterwards edits re-apply automatically."u8);
            return;
        }

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

        DrawPriority(selected);
    }

    // Commit only when the edit ends — priority pushes straight to Penumbra with a redraw,
    // which must not fire per keystroke while typing a number. The pending value is owned
    // by the group it was typed for, so a selection switch mid-edit cannot misapply it.
    private (Guid Owner, int Value)? _priorityEdit;

    private void DrawPriority(DTextures.DTexture selected)
    {
        if (_priorityEdit is { } pending && pending.Owner != selected.Identifier)
            _priorityEdit = null;

        ImGui.SameLine();
        var priority = _priorityEdit?.Value ?? _overlayMods.EffectivePriority(selected);
        ImGui.SetNextItemWidth(90 * ImUtf8.GlobalScale);
        if (ImGui.InputInt("Priority", ref priority))
            _priorityEdit = (selected.Identifier, priority);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            if (_priorityEdit is { } edit && edit.Owner == selected.Identifier)
                _overlayMods.SetModPriority(selected, edit.Value);
            _priorityEdit = null;
        }
        else if (ImGui.IsItemDeactivated())
        {
            _priorityEdit = null;
        }

        if (ImGui.IsItemHovered())
            ImUtf8.HoverTooltip("When two canvas groups override the same file, the enabled one with the higher priority wins."u8);

        if (selected.Data.ModPriority != null)
        {
            ImGui.SameLine();
            if (ImUtf8.SmallButton("Default"u8))
                _overlayMods.SetModPriority(selected, null);
            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip("Return to the default priority from the settings."u8);
        }
    }
}
