using System;
using Dalamud.Plugin;
using System.Reflection;
using OtterGui.Log;
using DynamicTextureManager.Services;
using OtterGui.Services;

namespace DynamicTextureManager;

public sealed class DynamicTextureManager : IDalamudPlugin
{
    public string Name => "DynamicTextureManager";
    
    public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty;
    public static readonly string CommitHash =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown";
    
    public static readonly Logger Log = new();
    private readonly ServiceManager _services;

    public DynamicTextureManager(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            _services = ServiceProvider.CreateProvider(pluginInterface, Log, this);

            _services.EnsureRequiredServices();
        }
        catch (Exception exception)
        {
            Log.Fatal($"DynamicTextureManager v{Version} failed to load: {exception.Message}");
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _services?.Dispose();
    }
}
