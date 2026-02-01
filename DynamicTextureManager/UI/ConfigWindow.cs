using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using OtterGui.Services;
using OtterGui.Text;

namespace DynamicTextureManager.UI;

public class ConfigWindowPosition : IService
{
    public bool IsOpen { get; set; }
    public Vector2 Position { get; set; }
    public Vector2 Size { get; set; }
}

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration _configuration;
    private readonly ConfigWindowPosition _position;
    
    public ConfigWindow(Configuration configuration, ConfigWindowPosition position) : base("Dynamic Display Manager: Configuration")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(600, 400)
        };

        _configuration = configuration;
        _position = position;
    }

    public void Dispose() { }

    public override void Draw()
    {
        _position.Size = ImGui.GetWindowSize();
        _position.Position = ImGui.GetWindowPos();
        
        Checkbox("Auto Reload"u8, "Auto Reload self on save."u8, 
            _configuration.AutoReload, v => _configuration.AutoReload = v);

        ImGui.Spacing();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void Checkbox(ReadOnlySpan<byte> label, ReadOnlySpan<byte> tooltip, bool current, Action<bool> setter)
    {
        using var id = ImUtf8.PushId(label);
        var tmp = current;
        if (ImUtf8.Checkbox(""u8, ref tmp) && tmp != current)
        {
            setter(tmp);
            _configuration.Save();
        }

        ImGui.SameLine();
        ImUtf8.LabeledHelpMarker(label, tooltip);
    }
}
