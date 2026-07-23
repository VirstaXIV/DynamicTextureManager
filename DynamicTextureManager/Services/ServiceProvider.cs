using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.Events;
using DynamicTextureManager.Interop;
using DynamicTextureManager.ModGeneration;
using DynamicTextureManager.ModGeneration.Shaders;
using DynamicTextureManager.UI;
using OtterGui.Classes;
using OtterGui.Log;
using OtterGui.Raii;
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
                       .AddInterop()
                       .AddEvents()
                       .AddDTextures()
                       .AddUi()
                       .AddExistingService(dynamicTextureManager);
        
        services.AddIServices(typeof(ImRaii).Assembly);
        services.CreateProvider();
        return services;
    }

    private static ServiceManager AddDalamudServices(
        this ServiceManager services, IDalamudPluginInterface pluginInterface)
        => services.AddExistingService(pluginInterface)
                   .AddExistingService(pluginInterface.UiBuilder)
                   .AddDalamudService<ICommandManager>(pluginInterface)
                   .AddDalamudService<IGameGui>(pluginInterface)
                   .AddDalamudService<IChatGui>(pluginInterface)
                   .AddDalamudService<IFramework>(pluginInterface)
                   .AddDalamudService<IKeyState>(pluginInterface)
                   .AddDalamudService<INotificationManager>(pluginInterface)
                   .AddDalamudService<ITargetManager>(pluginInterface)
                   .AddDalamudService<IObjectTable>(pluginInterface)
                   .AddDalamudService<ITextureProvider>(pluginInterface)
                   .AddDalamudService<IDataManager>(pluginInterface)
                   .AddDalamudService<IGameInteropProvider>(pluginInterface)
                   .AddDalamudService<IPluginLog>(pluginInterface);
    
    private static ServiceManager AddMeta(this ServiceManager services)
        => services.AddSingleton<MessageService>()
                    .AddSingleton<FilenameService>()
                    .AddSingleton<FrameworkManager>()
                    .AddSingleton<SaveService>()
                    .AddSingleton<Configuration>()
                    .AddSingleton<CommandService>();

    private static ServiceManager AddInterop(this ServiceManager services)
        => services.AddSingleton<PenumbraService>()
                   .AddSingleton<CharacterModelState>()
                   .AddSingleton<SkinColorReader>()
                   .AddSingleton<TargetResolver>()
                   .AddSingleton<ShaderHandlerRegistry>()
                   .AddSingleton<SourceFileProvider>()
                   .AddSingleton<ModWriter>()
                   .AddSingleton<OverlayModManager>()
                   .AddSingleton<RowHighlighter>()
                   .AddSingleton<EditPreviewer>()
                   .AddSingleton<TextureIO>()
                   .AddSingleton<TextureCompositor>()
                   .AddSingleton<ModelUvReader>()
                   .AddSingleton<DecalLibrary>()
                   .AddSingleton<CompositePreviewCache>();

    private static ServiceManager AddEvents(this ServiceManager services)
        => services.AddSingleton<DTextureChanged>();
    
    private static ServiceManager AddDTextures(this ServiceManager services)
        => services.AddSingleton<DTextureManager>()
            .AddSingleton<DTextureStorage>()
            .AddSingleton<DTextureFileSystem>();


    private static ServiceManager AddUi(this ServiceManager services)
        => services.AddSingleton<MainWindow>()
                    .AddSingleton<MainWindowPosition>()
                    .AddSingleton<ConfigWindow>()
                    .AddSingleton<ConfigWindowPosition>()
                    .AddSingleton<DecalLibraryPanel>()
                    .AddSingleton<DecalLibraryWindow>()
                    .AddSingleton<DTMWindowSystem>()
                    .AddSingleton<DTMFileSystemSelector>()
                    .AddSingleton<UI.Panels.SourceTab>()
                    .AddSingleton<UI.Panels.DecalsTab>()
                    .AddSingleton<UI.Panels.TextureViewerTab>()
                    .AddSingleton<DTMPanel>();
}
