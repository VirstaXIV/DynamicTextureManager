using System;
using System.Linq;
using Dalamud.Plugin;
using System.Reflection;
using System.Text;
using OtterGui.Log;
using DynamicTextureManager.Services;
using DynamicTextureManager.UI;
using OtterGui.Classes;
using OtterGui.Services;

namespace DynamicTextureManager;

public sealed class DynamicTextureManager : IDalamudPlugin
{
    public string Name => "DynamicTextureManager";
    
    public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty;
    public static readonly string CommitHash =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown";
    
    public static readonly Logger Log = new();
    public static MessageService Messager { get; private set; } = null!;
    private readonly ServiceManager _services;

    public DynamicTextureManager(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            _services = ServiceProvider.CreateProvider(pluginInterface, Log, this);
            Messager  = _services.GetService<MessageService>();

            _services.EnsureRequiredServices();
            Colors.SetColors(_services.GetService<Configuration>());
            _services.GetService<DTMWindowSystem>();
            _services.GetService<CommandService>();
            Log.Information($"Dynamic Texture Manager v{Version} loaded successfully.");
        }
        catch (Exception exception)
        {
            Log.Fatal($"Dynamic Texture Manager v{Version} failed to load: {exception.Message}");
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _services?.Dispose();
    }
}
