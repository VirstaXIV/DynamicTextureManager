using System;
using Dalamud.Plugin;
using System.Reflection;
using DynamicTextureManager.Services;
using DynamicTextureManager.UI;
using Luna;

namespace DynamicTextureManager;

public sealed class DynamicTextureManager : IDalamudPlugin
{
    public string Name => "DynamicTextureManager";

    public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty;

    public static readonly MainLogger Log = new("DynamicTextureManager");

    public static MessageService Messager { get; private set; } = null!;
    private readonly ServiceManager _services;

    public DynamicTextureManager(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            _services = ServiceProvider.CreateProvider(pluginInterface, Log, this);
            _services.EnsureRequiredServices();
            Messager  = _services.GetService<MessageService>();
            Colors.SetColors(_services.GetService<Configuration>());
            ModGeneration.FinishMapping.Sync(_services.GetService<Configuration>());
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
