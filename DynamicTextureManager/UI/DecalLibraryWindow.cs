using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DynamicTextureManager.Services;
using OtterGui.Raii;
using OtterGui.Text;

namespace DynamicTextureManager.UI;

/// <summary>
/// Standalone window around <see cref="DecalLibraryPanel"/> — the resource library: decal
/// images plus imported effect patterns, both stored and managed in one place. Normally
/// opened from the main window's title bar; the Decals tab opens it as a picker, where
/// clicking a decal (or importing a new one) hands it back to the tab and closes the window.
/// </summary>
public class DecalLibraryWindow : Window
{
    private readonly DecalLibraryPanel _panel;

    private string              _pickerPrompt = string.Empty;
    private Action<DecalEntry>? _onPick;

    public DecalLibraryWindow(DecalLibraryPanel panel)
        : base("Resource Library###dtmDecalLibrary")
    {
        _panel = panel;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void OpenAsPicker(string prompt, Action<DecalEntry> onPick)
    {
        _pickerPrompt = prompt;
        _onPick       = onPick;
        IsOpen        = true;
        BringToFront();
    }

    private bool _focusEffects;

    /// <summary> Open the library on the Effect Patterns tab (manage mode). </summary>
    public void OpenEffects()
    {
        _onPick       = null;
        _focusEffects = true;
        IsOpen        = true;
        BringToFront();
    }

    public override void Draw()
    {
        if (_onPick == null)
        {
            var focusEffects = _focusEffects;
            _focusEffects = false;
            using var tabs = ImUtf8.TabBar("##resourceTabs"u8);
            if (tabs)
            {
                using (var tab = ImUtf8.TabItem("Decals"u8))
                {
                    if (tab)
                        _panel.Draw();
                }

                using (var tab = ImUtf8.TabItem("Effect Patterns"u8,
                           focusEffects ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                {
                    if (tab)
                        _panel.DrawEffects();
                }
            }

            return;
        }

        ImUtf8.TextWrapped(_pickerPrompt);
        if (ImUtf8.SmallButton("Cancel"u8))
        {
            _onPick = null;
            IsOpen  = false;
            return;
        }

        ImGui.Separator();
        _panel.Draw(entry =>
        {
            var pick = _onPick;
            _onPick  = null;
            IsOpen   = false;
            pick?.Invoke(entry);
        });
    }

    public override void OnClose()
    {
        _onPick = null;
        base.OnClose();
    }
}
