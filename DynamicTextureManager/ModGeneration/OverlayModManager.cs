using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.Events;
using DynamicTextureManager.Interop;
using DynamicTextureManager.Services;
using IService = Luna.IService;
using Penumbra.Api.Enums;
using Penumbra.GameData.Files.MaterialStructs;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Orchestrates the lifecycle of generated overlay mods: building the mod folder from a
/// dTexture's edits, registering it with Penumbra, rebuilding on re-apply and deleting it.
/// Also keeps the plugin's build state in sync with what happens inside Penumbra
/// (mods deleted or renamed there) and surfaces orphaned generated mods.
/// </summary>
public sealed partial class OverlayModManager : IService, IDisposable
{
    private const string GeneratedModPrefix = "DTM_";
    private const long   AutoApplyDelayMs   = 1500;

    private readonly PenumbraService       penumbra;
    private readonly SourceFileProvider    sourceFiles;
    private readonly ModWriter             modWriter;
    private readonly SaveService           saveService;
    private readonly Configuration         config;
    private readonly DTextureStorage       storage;
    private readonly DTextureChanged       dTextureChanged;
    private readonly IFramework            framework;
    private readonly TextureIO             textureIO;
    private readonly TextureCompositor     compositor;
    private readonly Shaders.ShaderHandlerRegistry shaderHandlers;
    private readonly ModelUvReader         uvReader;
    private readonly Interop.HairColorReader hairColors;
    private readonly DecalLibrary          decals;

    public OverlayModManager(PenumbraService penumbra, SourceFileProvider sourceFiles, ModWriter modWriter, SaveService saveService,
        Configuration config, DTextureStorage storage, DTextureChanged dTextureChanged, IFramework framework, TextureIO textureIO,
        TextureCompositor compositor, Shaders.ShaderHandlerRegistry shaderHandlers, ModelUvReader uvReader,
        Interop.HairColorReader hairColors, DecalLibrary decals)
    {
        this.decals = decals;
        this.penumbra        = penumbra;
        this.sourceFiles     = sourceFiles;
        this.modWriter       = modWriter;
        this.saveService     = saveService;
        this.config          = config;
        this.storage         = storage;
        this.dTextureChanged = dTextureChanged;
        this.framework       = framework;
        this.textureIO       = textureIO;
        this.compositor      = compositor;
        this.shaderHandlers  = shaderHandlers;
        this.uvReader        = uvReader;
        this.hairColors      = hairColors;

        this.penumbra.Attached   += ReconcileMissingMods;
        this.penumbra.ModDeleted += OnPenumbraModDeleted;
        this.penumbra.ModMoved   += OnPenumbraModMoved;
        this.dTextureChanged.Subscribe(OnDTextureChanged, DTextureChanged.Priority.OverlayModManager);
        this.framework.Update    += OnFrameworkUpdate;

        if (this.penumbra.Available)
            ReconcileMissingMods();
    }

    public void Dispose()
    {
        penumbra.Attached   -= ReconcileMissingMods;
        penumbra.ModDeleted -= OnPenumbraModDeleted;
        penumbra.ModMoved   -= OnPenumbraModMoved;
        dTextureChanged.Unsubscribe(OnDTextureChanged);
        framework.Update    -= OnFrameworkUpdate;
    }

    public string LastResult { get; private set; } = string.Empty;

    public bool Busy { get; private set; }

    private (string Dir, Guid CollectionId, string CollectionName, bool Enabled, long FetchedMs)? _stateCache;
    private (Dictionary<string, bool> States, long FetchedMs)?                                    _allStatesCache;
    private (List<(string Directory, string Name)> Mods, long FetchedMs)?                         _orphanCache;
    private (DTexture DTexture, long QueuedMs)?                                                   _pendingAutoApply;

    public string ModDirectoryName(DTexture dTexture)
        => dTexture.Data.OutputModDirectory.Length > 0
            ? dTexture.Data.OutputModDirectory
            : $"DTM_{dTexture.Incognito}";

    private static string ModName(DTexture dTexture)
        => $"DTM - {dTexture.Name}";

    private bool Fail(string message)
    {
        LastResult = message;
        DynamicTextureManager.Log.Warning($"Overlay mod build: {message}");
        return false;
    }
}
