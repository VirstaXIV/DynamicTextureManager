using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using OtterGui.Classes;
using OtterGui.Log;
using OtterGui.Services;

namespace DynamicTextureManager.Services;

public static class ServiceProvider
{
    public static ServiceManager CreateProvider(
        IDalamudPluginInterface pluginInterface, Logger log, DynamicTextureManager dynamicTextureManager)
    {
        EventWrapperBase.ChangeLogger(log);

        var services = new ServiceManager(log)
                       .AddExistingService(log)
                       .AddDalamudServices(pluginInterface)
                       .AddMeta()
                       .AddExistingService(dynamicTextureManager);
        
        return services;
    }

    private static ServiceManager AddDalamudServices(
        this ServiceManager services, IDalamudPluginInterface pluginInterface)
        => services.AddExistingService(pluginInterface)
                   .AddDalamudService<ICommandManager>(pluginInterface);
    
    private static ServiceManager AddMeta(this ServiceManager services)
        => services.AddSingleton<MessageService>()
                   .AddSingleton<FrameworkManager>()
                   .AddSingleton<Configuration>()
                   .AddSingleton<CommandService>();
}
