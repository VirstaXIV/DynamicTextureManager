using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using OtterGui.Services;

namespace DynamicTextureManager.UI;

public class MainWindowPosition : IService
{
    public bool IsOpen { get; set; }
    public Vector2 Position { get; set; }
    public Vector2 Size { get; set; }
}

public class MainWindow : Window, IDisposable
{
    private readonly Configuration _configuration;
    private readonly MainWindowPosition _position;
    private readonly ConfigWindow _configWindow;
    private readonly List<string> _items = new();
    private int _selectedIndex = -1;
    private const float SidebarWidth = 300f;
    
    private readonly DTMFileSystemSelector _selector;
    private readonly DTMPanel _panel;
    
    public MainWindow(DTMFileSystemSelector selector, DTMPanel panel, Configuration configuration,
        MainWindowPosition position, ConfigWindow configWindow) : base("Dynamic Texture Manager")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(800, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        _position = position;
        _configuration = configuration;
        _configWindow = configWindow;
        _selector = selector;
        _panel = panel;

        TitleBarButtons = new()
        {
            new TitleBarButton()
            {
                Icon = FontAwesomeIcon.Cog,
                Click = (msg) => { OpenConfigUi(); },
                IconOffset = new(2, 1),
                ShowTooltip = () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("Show Config");
                    ImGui.EndTooltip();
                }
            }
        };
    }

    public void Dispose()
    {
        _selector.Dispose();
        _panel.Dispose();
    }

    public override void Draw()
    {
        _position.Size = ImGui.GetWindowSize();
        _position.Position = ImGui.GetWindowPos();

        _selector.Draw();
        ImGui.SameLine();
        _panel.Draw();
    }

    public void OpenConfigUi()
    {
        _configWindow.Toggle();
    }
}
