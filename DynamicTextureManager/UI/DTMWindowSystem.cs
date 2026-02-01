using System;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace DynamicTextureManager.UI;

public class DTMWindowSystem: IDisposable
{
    private readonly WindowSystem _windowSystem = new("DynamicTextureManager");
    private readonly IUiBuilder _uiBuilder;
    private readonly MainWindow _mainWindow;
    private readonly ConfigWindow _configWindow;
    
    public DTMWindowSystem(
        IUiBuilder uiBuilder,
        MainWindow mainWindow,
        ConfigWindow configWindow)
    {
        _uiBuilder = uiBuilder;
        _mainWindow = mainWindow;
        _configWindow = configWindow;

        _windowSystem.AddWindow(mainWindow);
        _windowSystem.AddWindow(configWindow);
        
        _uiBuilder.OpenMainUi += _mainWindow.Toggle;
        _uiBuilder.Draw += _windowSystem.Draw;
        _uiBuilder.OpenConfigUi += _mainWindow.OpenConfigUi;
    }
    
    public bool ConfigWindowOpened()
    {
        return _configWindow.IsOpen;
    }

    public void ToggleConfigWindow()
    {
        _configWindow.Toggle();
    }
    
    public void ToggleMainWindow()
    {
        _mainWindow.Toggle();
    }

    public void Dispose()
    {
        _uiBuilder.OpenMainUi -= _mainWindow.Toggle;
        _uiBuilder.Draw -= _windowSystem.Draw;
        _uiBuilder.OpenConfigUi -= _mainWindow.OpenConfigUi;
    }
}