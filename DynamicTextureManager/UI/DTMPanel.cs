using System;
using System.Numerics;
using Dalamud.Interface;
using DynamicTextureManager.Interop;
using DynamicTextureManager.ModGeneration;
using DynamicTextureManager.UI.Panels;
using ImSharp;
using Luna;

namespace DynamicTextureManager.UI;

public class DTMPanel : IDisposable
{
    private readonly DTMFileSystemDrawer _selector;
    private readonly OverlayModManager _overlayMods;
    private readonly PenumbraService _penumbra;
    private readonly EditPreviewer _previewer;
    private readonly SourceTab _sourceTab;
    private readonly DecalsTab _decalsTab;
    private readonly Configuration _config;
    private readonly PanelHeader _header;

    public DTMPanel(DTMFileSystemDrawer selector, OverlayModManager overlayMods, PenumbraService penumbra, EditPreviewer previewer,
        SourceTab sourceTab, DecalsTab decalsTab, Configuration config)
    {
        _selector     = selector;
        _overlayMods  = overlayMods;
        _penumbra     = penumbra;
        _previewer    = previewer;
        _sourceTab    = sourceTab;
        _decalsTab    = decalsTab;
        _config       = config;
        _header       = new PanelHeader(this);
    }

    /// <summary> The split-button header over the panel: build + auto-rebuild toggle on the left, delete on the right, selection name in the middle. </summary>
    private sealed class PanelHeader : SplitButtonHeader
    {
        private readonly DTMPanel _panel;
        private string           _lastName = string.Empty;
        private StringU8         _text     = StringU8.Empty;

        public PanelHeader(DTMPanel panel)
        {
            _panel = panel;
            LeftButtons.AddButton(new ApplyButton(panel), 100);
            LeftButtons.AddButton(new AutoRebuildButton(panel), 90);
            RightButtons.AddButton(new DeleteModButton(panel), 100);
        }

        public override ReadOnlySpan<byte> Text
            => _text;

        public override void Draw(Vector2 size)
        {
            var name = _panel.SelectionName;
            if (!ReferenceEquals(name, _lastName))
            {
                _lastName = name;
                _text     = new StringU8(name);
            }

            var color = new Rgba32(ColorId.HeaderButtons.Value());
            using var _ = ImGuiColor.Text.Push(color).Push(ImGuiColor.Border, color);
            base.Draw(size with { Y = Im.Style.FrameHeight });
        }
    }

    private sealed class ApplyButton(DTMPanel panel) : BaseIconButton<AwesomeIcon>
    {
        public override ReadOnlySpan<byte> Label
            => "##apply"u8;

        public override AwesomeIcon Icon
            => FontAwesomeIcon.Hammer;

        public override bool HasTooltip
            => true;

        public override void DrawTooltip()
            => Im.Text(
                "Build: bake the current edits into the generated Penumbra mod (and enable it).\nUse the \"Enabled\" checkbox below to toggle the mod on or off."u8);

        public override bool Enabled
            => panel._selector.Selected != null && !panel._overlayMods.Busy;

        public override void OnClick()
            => panel.Apply();
    }

    private sealed class AutoRebuildButton(DTMPanel panel) : BaseIconButton<AwesomeIcon>
    {
        public override ReadOnlySpan<byte> Label
            => "##autoRebuild"u8;

        public override AwesomeIcon Icon
            => panel._config.AutoReload ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff;

        public override bool HasTooltip
            => true;

        public override void DrawTooltip()
            => Im.Text(panel._config.AutoReload
                ? "Auto-rebuild is ON: edits rebuild the built mod automatically.\nClick to turn off — edits then show only in the preview until you press Build."u8
                : "Auto-rebuild is OFF: edits show only in the preview.\nClick to turn on, or press Build to bake the current state."u8);

        public override void OnClick()
        {
            panel._config.AutoReload = !panel._config.AutoReload;
            panel._config.Save();
        }
    }

    private sealed class DeleteModButton(DTMPanel panel) : BaseIconButton<AwesomeIcon>
    {
        public override ReadOnlySpan<byte> Label
            => "##deleteMod"u8;

        public override AwesomeIcon Icon
            => FontAwesomeIcon.Trash;

        public override bool HasTooltip
            => true;

        public override void DrawTooltip()
            => Im.Text("Delete the generated Penumbra mod of this canvas group (keeps the canvas group itself)."u8);

        public override bool Enabled
            => panel._selector.Selected != null && panel._selector.Selected.Data.OutputModDirectory.Length > 0;

        public override void OnClick()
            => panel.DeleteMod();
    }

    public void Dispose()
    { }

    /// <summary> The header is mounted as the layout's RightHeader so it sits flush above the panel child. </summary>
    public IHeader Header
        => _header;

    public void Draw()
        => DrawPanel();

    private string SelectionName
        => _selector.Selected == null ? "No Selection" : _selector.Selected.Name;

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
        // The layout's right panel child already provides the border and padding —
        // draw straight into it, or the content floats inside a second inset box.
        if (_selector.Selected == null)
            return;

        var selected = _selector.Selected;

        if (_overlayMods.LastResult.Length > 0)
            Im.Text(_overlayMods.LastResult);
        DrawGeneratedModLine(selected);
        Im.Separator();

        var avail = Im.ContentRegion.Available;
        var leftWidth = MathF.Min(MathF.Max(avail.X * 0.48f, 380f * Im.Style.GlobalScale),
            MathF.Max(avail.X - 340f * Im.Style.GlobalScale, 260f * Im.Style.GlobalScale));
        using (var left = Im.Child.Begin("##controls"u8, new Vector2(leftWidth, avail.Y), false))
        {
            if (left)
            {
                if (Im.Tree.Header("Sources"u8,
                        selected.Data.Source.IsEmpty ? TreeNodeFlags.DefaultOpen : TreeNodeFlags.None))
                {
                    _sourceTab.Draw(selected);
                    Im.Separator();
                }

                _decalsTab.DrawControls(selected);
            }
        }

        Im.Line.Same();
        using (var right = Im.Child.Begin("##visuals"u8, Im.ContentRegion.Available, false))
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
                Im.TextWrapped(
                    "Not built yet — edits show only in the preview. Press the hammer button above to build the Penumbra mod and see them in-game; afterwards edits re-apply automatically."u8);
            return;
        }

        Im.Text($"Generated Mod: {selected.Data.OutputModDirectory}");
        if (!_penumbra.Available)
            return;

        var state = _overlayMods.QueryModState(selected);
        if (state != null)
        {
            Im.Line.Same();
            var enabled = state.Value.Enabled;
            if (Im.Checkbox("Enabled"u8, ref enabled))
                _overlayMods.SetModEnabled(selected, enabled);
            Im.Tooltip.OnHover(HoveredFlags.None,
                $"Enable or disable the generated mod in collection \"{state.Value.CollectionName}\".");
        }

        Im.Line.Same();
        if (Im.SmallButton("Open in Penumbra"u8))
            _penumbra.OpenModInPenumbra(selected.Data.OutputModDirectory);
        Im.Tooltip.OnHover("Open the generated mod in Penumbra's mod tab."u8);

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

        Im.Line.Same();
        var priority = _priorityEdit?.Value ?? _overlayMods.EffectivePriority(selected);
        Im.Item.SetNextWidthScaled(90);
        if (Im.Input.Scalar("Priority"u8, ref priority, 1, 100))
            _priorityEdit = (selected.Identifier, priority);
        if (Im.Item.DeactivatedAfterEdit)
        {
            if (_priorityEdit is { } edit && edit.Owner == selected.Identifier)
                _overlayMods.SetModPriority(selected, edit.Value);
            _priorityEdit = null;
        }
        else if (Im.Item.Deactivated)
        {
            _priorityEdit = null;
        }

        Im.Tooltip.OnHover("When two canvas groups override the same file, the enabled one with the higher priority wins."u8);

        if (selected.Data.ModPriority != null)
        {
            Im.Line.Same();
            if (Im.SmallButton("Default"u8))
                _overlayMods.SetModPriority(selected, null);
            Im.Tooltip.OnHover("Return to the default priority from the settings."u8);
        }
    }
}
