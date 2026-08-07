using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using DynamicTextureManager.Services;
using ImSharp;

namespace DynamicTextureManager.UI;

/// <summary>
/// Standalone window around <see cref="DecalLibraryPanel"/> — the resource library: decal
/// images plus imported effect and marking patterns, all stored and managed in one place.
/// Normally opened from the main window's title bar; the Decals tab opens it as a picker,
/// where clicking a decal (or importing a new one) hands it back to the tab and closes the
/// window.
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

    private bool _focusPatterns;

    /// <summary> Open the library on the Marking Patterns tab (manage mode). </summary>
    public void OpenPatterns()
    {
        _onPick        = null;
        _focusPatterns = true;
        IsOpen         = true;
        BringToFront();
    }

    public override void Draw()
    {
        // The shared ImSharp context attaches on a framework tick after service
        // construction — Im.* calls before that dereference an empty context.
        if (!ImSharpConfiguration.IsInitialized)
            return;

        if (_onPick == null)
        {
            var focusEffects  = _focusEffects;
            var focusPatterns = _focusPatterns;
            _focusEffects  = false;
            _focusPatterns = false;
            using var tabs = Im.TabBar.Begin("##resourceTabs"u8);
            if (tabs)
            {
                using (var tab = tabs.Item("Decals"u8))
                {
                    if (tab)
                        _panel.Draw();
                }

                using (var tab = tabs.Item("Effect Patterns"u8,
                           focusEffects ? TabItemFlags.SetSelected : TabItemFlags.None))
                {
                    if (tab)
                        _panel.DrawEffects();
                }

                using (var tab = tabs.Item("Marking Patterns"u8,
                           focusPatterns ? TabItemFlags.SetSelected : TabItemFlags.None))
                {
                    if (tab)
                        _panel.DrawPatterns();
                }
            }

            return;
        }

        Im.TextWrapped(_pickerPrompt);
        if (Im.SmallButton("Cancel"u8))
        {
            _onPick = null;
            IsOpen  = false;
            return;
        }

        Im.Separator();
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
