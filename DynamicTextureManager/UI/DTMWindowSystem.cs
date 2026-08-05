using System;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace DynamicTextureManager.UI;

public class DTMWindowSystem: IDisposable
{
    private readonly WindowSystem _windowSystem = new("DynamicTextureManager");
    private readonly IUiBuilder _uiBuilder;
    private readonly MainWindow _mainWindow;

    public DTMWindowSystem(
        IUiBuilder uiBuilder,
        MainWindow mainWindow,
        ConfigWindow configWindow,
        DecalLibraryWindow decalLibraryWindow)
    {
        _uiBuilder = uiBuilder;
        _mainWindow = mainWindow;

        _windowSystem.AddWindow(mainWindow);
        _windowSystem.AddWindow(configWindow);
        _windowSystem.AddWindow(decalLibraryWindow);

        _uiBuilder.OpenMainUi += _mainWindow.Toggle;
        _uiBuilder.Draw += _windowSystem.Draw;
        _uiBuilder.OpenConfigUi += _mainWindow.OpenConfigUi;
    }

    public void Dispose()
    {
        _uiBuilder.OpenMainUi -= _mainWindow.Toggle;
        _uiBuilder.Draw -= _windowSystem.Draw;
        _uiBuilder.OpenConfigUi -= _mainWindow.OpenConfigUi;
    }
}