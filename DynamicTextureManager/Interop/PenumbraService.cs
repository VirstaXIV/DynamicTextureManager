using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin;
using OtterGui.Log;
using OtterGui.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace DynamicTextureManager.Interop;

/// <summary>
/// Wraps all Penumbra IPC used by the plugin. Availability follows Penumbra's
/// Initialized/Disposed events; all calls throw IpcNotReadyError when Penumbra is absent,
/// so consumers should check <see cref="Available"/> first.
/// </summary>
public sealed class PenumbraService : IDisposable, IService
{
    public const int RequiredBreakingVersion = 5;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Logger                  _log;

    private readonly EventSubscriber                 _initializedEvent;
    private readonly EventSubscriber                 _disposedEvent;
    private readonly EventSubscriber<string>         _modDeletedEvent;
    private readonly EventSubscriber<string>         _modAddedEvent;
    private readonly EventSubscriber<string, string> _modMovedEvent;

    private readonly ApiVersion                    _apiVersion;
    private readonly GetModDirectory               _getModDirectory;
    private readonly GetModList                    _getModList;
    private readonly AddMod                        _addMod;
    private readonly DeleteMod                     _deleteMod;
    private readonly ReloadMod                     _reloadMod;
    private readonly SetModPath                    _setModPath;
    private readonly TrySetMod                     _trySetMod;
    private readonly TrySetModPriority             _trySetModPriority;
    private readonly GetCurrentModSettings         _getCurrentModSettings;
    private readonly GetAllModSettings             _getAllModSettings;
    private readonly GetCollectionForObject        _getCollectionForObject;
    private readonly GetGameObjectResourceTrees    _getGameObjectResourceTrees;
    private readonly ResolvePlayerPath             _resolvePlayerPath;
    private readonly ConvertTextureData            _convertTextureData;
    private readonly ConvertTextureFile            _convertTextureFile;
    private readonly RedrawObject                  _redrawObject;
    private readonly RedrawAll                     _redrawAll;
    private readonly OpenMainWindow                _openMainWindow;
    private readonly AddTemporaryModAll            _addTemporaryModAll;
    private readonly RemoveTemporaryModAll         _removeTemporaryModAll;

    public bool Available { get; private set; }

    public (int Breaking, int Features) Version { get; private set; }

    /// <summary> Fired when Penumbra becomes available (initial attach or reload). </summary>
    public event Action? Attached;

    /// <summary> Fired when Penumbra is disposed. </summary>
    public event Action? Detached;

    /// <summary> Fired when a mod is deleted in Penumbra, with its directory name. </summary>
    public event Action<string>? ModDeleted;

    /// <summary> Fired when a mod is added in Penumbra, with its directory name. </summary>
    public event Action<string>? ModAdded;

    /// <summary> Fired when a mod directory is moved in Penumbra, with old and new directory names. </summary>
    public event Action<string, string>? ModMoved;

    public PenumbraService(IDalamudPluginInterface pi, Logger log)
    {
        _pluginInterface = pi;
        _log             = log;

        _apiVersion                 = new ApiVersion(pi);
        _getModDirectory            = new GetModDirectory(pi);
        _getModList                 = new GetModList(pi);
        _addMod                     = new AddMod(pi);
        _deleteMod                  = new DeleteMod(pi);
        _reloadMod                  = new ReloadMod(pi);
        _setModPath                 = new SetModPath(pi);
        _trySetMod                  = new TrySetMod(pi);
        _trySetModPriority          = new TrySetModPriority(pi);
        _getCurrentModSettings      = new GetCurrentModSettings(pi);
        _getAllModSettings          = new GetAllModSettings(pi);
        _getCollectionForObject     = new GetCollectionForObject(pi);
        _getGameObjectResourceTrees = new GetGameObjectResourceTrees(pi);
        _resolvePlayerPath          = new ResolvePlayerPath(pi);
        _convertTextureData         = new ConvertTextureData(pi);
        _convertTextureFile         = new ConvertTextureFile(pi);
        _redrawObject               = new RedrawObject(pi);
        _redrawAll                  = new RedrawAll(pi);
        _openMainWindow             = new OpenMainWindow(pi);
        _addTemporaryModAll         = new AddTemporaryModAll(pi);
        _removeTemporaryModAll      = new RemoveTemporaryModAll(pi);

        _initializedEvent = Initialized.Subscriber(pi, OnPenumbraInitialized);
        _disposedEvent    = Disposed.Subscriber(pi, OnPenumbraDisposed);
        _modDeletedEvent  = Penumbra.Api.IpcSubscribers.ModDeleted.Subscriber(pi, d => ModDeleted?.Invoke(d));
        _modAddedEvent    = Penumbra.Api.IpcSubscribers.ModAdded.Subscriber(pi, d => ModAdded?.Invoke(d));
        _modMovedEvent    = Penumbra.Api.IpcSubscribers.ModMoved.Subscriber(pi, (o, n) => ModMoved?.Invoke(o, n));

        OnPenumbraInitialized();
    }

    public void Dispose()
    {
        _initializedEvent.Dispose();
        _disposedEvent.Dispose();
        _modDeletedEvent.Dispose();
        _modAddedEvent.Dispose();
        _modMovedEvent.Dispose();
        Available = false;
    }

    private void OnPenumbraInitialized()
    {
        try
        {
            Version = _apiVersion.Invoke();
        }
        catch (Exception)
        {
            Available = false;
            return;
        }

        if (Version.Breaking != RequiredBreakingVersion)
        {
            _log.Warning(
                $"Penumbra API version {Version.Breaking}.{Version.Features} does not match required breaking version {RequiredBreakingVersion}, disabling Penumbra integration.");
            Available = false;
            return;
        }

        Available = true;
        _log.Information($"Attached to Penumbra API version {Version.Breaking}.{Version.Features}.");
        Attached?.Invoke();
    }

    private void OnPenumbraDisposed()
    {
        Available = false;
        Detached?.Invoke();
    }

    public string GetModDirectory()
        => _getModDirectory.Invoke();

    /// <summary> All installed mods as (directory name, mod name). </summary>
    public Dictionary<string, string> GetModList()
        => _getModList.Invoke();

    public PenumbraApiEc AddMod(string modDirectory)
        => _addMod.Invoke(modDirectory);

    public PenumbraApiEc DeleteMod(string modDirectory)
        => _deleteMod.Invoke(modDirectory);

    public PenumbraApiEc ReloadMod(string modDirectory)
        => _reloadMod.Invoke(modDirectory);

    public PenumbraApiEc SetModPath(string modDirectory, string newPath)
        => _setModPath.Invoke(modDirectory, newPath);

    public PenumbraApiEc TrySetMod(Guid collectionId, string modDirectory, bool enabled)
        => _trySetMod.Invoke(collectionId, modDirectory, enabled);

    public PenumbraApiEc TrySetModPriority(Guid collectionId, string modDirectory, int priority)
        => _trySetModPriority.Invoke(collectionId, modDirectory, priority);

    /// <summary> Enabled state and priority of a mod in a collection, or null if the mod or collection is unknown. </summary>
    public (bool Enabled, int Priority)? GetModSettings(Guid collectionId, string modDirectory)
    {
        var (ec, settings) = _getCurrentModSettings.Invoke(collectionId, modDirectory);
        if (ec is not PenumbraApiEc.Success || settings == null)
            return null;

        return (settings.Value.Item1, settings.Value.Item2);
    }

    /// <summary> Enabled state of every mod in a collection by directory name, or null on failure. One IPC call. </summary>
    public Dictionary<string, bool>? GetAllModEnabledStates(Guid collectionId)
    {
        var (ec, settings) = _getAllModSettings.Invoke(collectionId);
        if (ec is not PenumbraApiEc.Success || settings == null)
            return null;

        var ret = new Dictionary<string, bool>(settings.Count);
        foreach (var (dir, value) in settings)
            ret[dir] = value.Item1;
        return ret;
    }

    public (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection) GetCollectionForObject(int gameObjectIdx)
        => _getCollectionForObject.Invoke(gameObjectIdx);

    public ResourceTreeDto?[] GetGameObjectResourceTrees(bool withUiData, params ushort[] gameObjectIndices)
        => _getGameObjectResourceTrees.Invoke(withUiData, gameObjectIndices);

    /// <summary> Resolve a game path through the player's current collection to an actual file path. </summary>
    public string ResolvePlayerPath(string gamePath)
        => _resolvePlayerPath.Invoke(gamePath);

    /// <summary> Let Penumbra BC-compress raw RGBA data and write it as a .tex (or other) file. </summary>
    public Task ConvertTextureData(byte[] rgbaData, int width, string outputFile, TextureType textureType, bool mipMaps = true)
        => _convertTextureData.Invoke(rgbaData, width, outputFile, textureType, mipMaps);

    public Task ConvertTextureFile(string inputFile, string outputFile, TextureType textureType, bool mipMaps = true)
        => _convertTextureFile.Invoke(inputFile, outputFile, textureType, mipMaps);

    public void RedrawObject(int gameObjectIndex)
        => _redrawObject.Invoke(gameObjectIndex);

    public void RedrawAll()
        => _redrawAll.Invoke();

    /// <summary> Open Penumbra's main window on the mod tab with the given mod selected. </summary>
    public PenumbraApiEc OpenModInPenumbra(string modDirectory)
        => _openMainWindow.Invoke(TabType.Mods, modDirectory);

    /// <summary> Add or update a temporary mod affecting all collections, e.g. for live previews. </summary>
    public PenumbraApiEc AddTemporaryModAll(string tag, Dictionary<string, string> paths, int priority)
        => _addTemporaryModAll.Invoke(tag, paths, string.Empty, priority);

    public PenumbraApiEc RemoveTemporaryModAll(string tag, int priority)
        => _removeTemporaryModAll.Invoke(tag, priority);

    /// <summary>
    /// Identify the Penumbra mod a resolved file path belongs to.
    /// Returns null for vanilla files and files outside the mod directory.
    /// </summary>
    public (string ModDirectory, string ModName)? IdentifyModOfFile(string actualPath)
    {
        if (!Available)
            return null;

        try
        {
            return IdentifyModOfFile(actualPath, GetModDirectory(), GetModList());
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not identify mod of {actualPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary> Bulk-friendly overload with the mod root and mod list already fetched. </summary>
    public static (string ModDirectory, string ModName)? IdentifyModOfFile(string actualPath, string modRoot,
        IReadOnlyDictionary<string, string> modList)
    {
        if (actualPath.Length == 0 || modRoot.Length == 0 || !System.IO.Path.IsPathRooted(actualPath))
            return null;

        var fullRoot = System.IO.Path.GetFullPath(System.IO.Path.TrimEndingDirectorySeparator(modRoot));
        var fullPath = System.IO.Path.GetFullPath(actualPath);
        if (!fullPath.StartsWith(fullRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        var relative  = fullPath[(fullRoot.Length + 1)..];
        var separator = relative.IndexOf(System.IO.Path.DirectorySeparatorChar);
        if (separator <= 0)
            return null;

        var modDirectory = relative[..separator];
        var modName      = modList.GetValueOrDefault(modDirectory, modDirectory);
        return (modDirectory, modName);
    }
}
