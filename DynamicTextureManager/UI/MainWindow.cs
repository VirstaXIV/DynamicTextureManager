using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using DynamicTextureManager.ModGeneration;
using Luna;
using Im = ImSharp.Im;
using IService = Luna.IService;
using TitleBarButton = Dalamud.Interface.Windowing.TitleBarButton;
using Window = Dalamud.Interface.Windowing.Window;

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

    private readonly DTMPanel _panel;
    private readonly SelectorLayout _layout;
    private readonly EditPreviewer _previewer;
    private readonly RowHighlighter _highlighter;

    public MainWindow(DTMFileSystemDrawer selector, DTMPanel panel, Configuration configuration,
        MainWindowPosition position, ConfigWindow configWindow, DecalLibraryWindow decalLibraryWindow,
        EditPreviewer previewer, RowHighlighter highlighter)
        : base("Dynamic Texture Manager")
    {
        _previewer   = previewer;
        _highlighter = highlighter;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(800, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        _position = position;
        _configuration = configuration;
        _configWindow = configWindow;
        _panel = panel;
        _layout = new SelectorLayout(selector, panel);

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
            },
            new TitleBarButton()
            {
                Icon = FontAwesomeIcon.Images,
                Click = (msg) => { decalLibraryWindow.Toggle(); },
                IconOffset = new(2, 1),
                ShowTooltip = () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("Decal Library");
                    ImGui.EndTooltip();
                }
            }
        };
    }

    public void Dispose()
    {
        _panel.Dispose();
    }

    public override void Draw()
    {
        // The shared ImSharp context attaches on a framework tick after service
        // construction — Im.* calls before that dereference an empty context.
        if (!ImSharp.ImSharpConfiguration.IsInitialized)
            return;

        _position.Size = ImGui.GetWindowSize();
        _position.Position = ImGui.GetWindowPos();

        _layout.Draw();
    }

    public override void OnClose()
    {
        // Temporary preview mods must never outlive the editing session.
        _previewer.Clear();
        _highlighter.Clear();
        base.OnClose();
    }

    public void OpenConfigUi()
    {
        _configWindow.Toggle();
    }

    /// <summary>
    /// Hosts the selector (with its filter header and button footer) next to the main panel.
    /// The selector keeps its old default width of 200 but can now be dragged up to half the window.
    /// </summary>
    private sealed class SelectorLayout : TwoPanelLayout
    {
        private TwoPanelWidth _width = new(200f, ScalingMode.Absolute);

        public SelectorLayout(DTMFileSystemDrawer selector, DTMPanel panel)
        {
            LeftHeader  = selector.Header;
            LeftPanel   = selector;
            LeftFooter  = selector.Footer;
            RightHeader = NopHeaderFooter.Instance;
            RightPanel  = new PanelAdapter(panel);
            RightFooter = NopHeaderFooter.Instance;
        }

        protected override float MinimumWidth
            => LeftFooter.MinimumWidth;

        protected override float MaximumWidth
            => Im.Window.Width * 0.5f;

        protected override void SetWidth(float width, ScalingMode mode)
            => _width = new TwoPanelWidth(width, mode);

        public void Draw()
            => Draw(_width);
    }

    /// <summary> The main panel draws its own header and children; it only needs wrapping into a layout panel. </summary>
    private sealed class PanelAdapter(DTMPanel panel) : IPanel
    {
        public ReadOnlySpan<byte> Id
            => "##dtmPanel"u8;

        public void Draw()
            => panel.Draw();
    }
}
