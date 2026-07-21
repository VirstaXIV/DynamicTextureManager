using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DynamicTextureManager.Services;
using OtterGui.Text;

namespace DynamicTextureManager.UI;

/// <summary>
/// Standalone window around <see cref="DecalLibraryPanel"/>. Normally opened from the main
/// window's title bar for managing the library; the Decals tab opens it as a picker, where
/// clicking a decal (or importing a new one) hands it back to the tab and closes the window.
/// </summary>
public class DecalLibraryWindow : Window
{
    private readonly DecalLibraryPanel _panel;

    private string              _pickerPrompt = string.Empty;
    private Action<DecalEntry>? _onPick;

    public DecalLibraryWindow(DecalLibraryPanel panel)
        : base("Decal Library")
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

    public override void Draw()
    {
        if (_onPick == null)
        {
            _panel.Draw();
            return;
        }

        ImUtf8.Text(_pickerPrompt);
        ImGui.SameLine();
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
