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
using OtterGui.Services;
using Penumbra.Api.Enums;
using Penumbra.GameData.Files.MaterialStructs;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Orchestrates the lifecycle of generated overlay mods: building the mod folder from a
/// dTexture's edits, registering it with Penumbra, rebuilding on re-apply and deleting it.
/// Also keeps the plugin's build state in sync with what happens inside Penumbra
/// (mods deleted or renamed there) and surfaces orphaned generated mods.
/// </summary>
public sealed class OverlayModManager : IService, IDisposable
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

    public OverlayModManager(PenumbraService penumbra, SourceFileProvider sourceFiles, ModWriter modWriter, SaveService saveService,
        Configuration config, DTextureStorage storage, DTextureChanged dTextureChanged, IFramework framework, TextureIO textureIO,
        TextureCompositor compositor, Shaders.ShaderHandlerRegistry shaderHandlers, ModelUvReader uvReader)
    {
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

    #region Lifecycle sync

    /// <summary> Queue an automatic debounced rebuild after an edit; only rebuilds mods that were already built. </summary>
    public void QueueAutoApply(DTexture dTexture)
    {
        if (!config.AutoReload || dTexture.Data.OutputModDirectory.Length == 0)
            return;

        _pendingAutoApply = (dTexture, Environment.TickCount64);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (_pendingAutoApply is not { } pending || Busy)
            return;
        if (Environment.TickCount64 - pending.QueuedMs < AutoApplyDelayMs)
            return;

        _pendingAutoApply = null;
        if (storage.Contains(pending.DTexture.Identifier))
            Apply(pending.DTexture);
    }

    /// <summary> A mod was deleted inside Penumbra: mark the matching dTexture as not built. </summary>
    private void OnPenumbraModDeleted(string modDirectory)
    {
        InvalidateCaches();
        foreach (var dTexture in storage.Where(d => string.Equals(d.Data.OutputModDirectory, modDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            dTexture.Data.OutputModDirectory = string.Empty;
            dTexture.Data.LastBuiltHash      = string.Empty;
            saveService.QueueSave(dTexture);
            DynamicTextureManager.Log.Information(
                $"Generated mod {modDirectory} was deleted in Penumbra, marked dTexture {dTexture.Incognito} as not built.");
        }
    }

    /// <summary> A mod directory was renamed inside Penumbra: follow it. </summary>
    private void OnPenumbraModMoved(string oldDirectory, string newDirectory)
    {
        InvalidateCaches();
        foreach (var dTexture in storage.Where(d => string.Equals(d.Data.OutputModDirectory, oldDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            dTexture.Data.OutputModDirectory = newDirectory;
            saveService.QueueSave(dTexture);
            DynamicTextureManager.Log.Information(
                $"Generated mod {oldDirectory} was renamed to {newDirectory} in Penumbra, updated dTexture {dTexture.Incognito}.");
        }
    }

    /// <summary> On dTexture deletion, optionally delete its generated mod. Never resaves the deleted dTexture. </summary>
    private void OnDTextureChanged(DTextureChanged.Type type, DTexture dTexture, DTextures.History.ITransaction? _)
    {
        if (type is not DTextureChanged.Type.Deleted || !config.DeleteModWithDTexture)
            return;

        var dir = dTexture.Data.OutputModDirectory;
        if (dir.Length == 0 || !penumbra.Available)
            return;

        try
        {
            var ec = penumbra.DeleteMod(dir);
            InvalidateCaches();
            if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged and not PenumbraApiEc.ModMissing)
                DynamicTextureManager.Log.Warning($"Could not delete generated mod {dir} of deleted dTexture: {ec}.");
            else
                DynamicTextureManager.Log.Information($"Deleted generated mod {dir} together with its dTexture.");
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not delete generated mod {dir}: {ex.Message}");
        }
    }

    /// <summary> Clear build state of dTextures whose generated mod no longer exists (e.g. deleted while the game was off). </summary>
    private void ReconcileMissingMods()
    {
        try
        {
            var modList = penumbra.GetModList();
            foreach (var dTexture in storage.Where(d => d.Data.OutputModDirectory.Length > 0))
            {
                if (modList.ContainsKey(dTexture.Data.OutputModDirectory))
                    continue;

                DynamicTextureManager.Log.Information(
                    $"Generated mod {dTexture.Data.OutputModDirectory} of dTexture {dTexture.Incognito} no longer exists, marked as not built.");
                dTexture.Data.OutputModDirectory = string.Empty;
                dTexture.Data.LastBuiltHash      = string.Empty;
                saveService.QueueSave(dTexture);
            }
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not reconcile generated mods with Penumbra: {ex.Message}");
        }
    }

    /// <summary> Generated mods in Penumbra that no dTexture claims, cached briefly for UI use. Never auto-deleted. </summary>
    public IReadOnlyList<(string Directory, string Name)> GetOrphanedMods()
    {
        if (!penumbra.Available)
            return [];

        if (_orphanCache is { } cache && Environment.TickCount64 - cache.FetchedMs < 5000)
            return cache.Mods;

        try
        {
            var claimed = storage
                .Select(d => d.Data.OutputModDirectory)
                .Where(d => d.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var orphans = penumbra.GetModList()
                .Where(kvp => kvp.Key.StartsWith(GeneratedModPrefix, StringComparison.OrdinalIgnoreCase) && !claimed.Contains(kvp.Key))
                .Select(kvp => (kvp.Key, kvp.Value))
                .OrderBy(m => m.Key)
                .ToList();
            _orphanCache = (orphans, Environment.TickCount64);
            return orphans;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not scan for orphaned mods: {ex.Message}");
            return [];
        }
    }

    /// <summary> Delete an orphaned generated mod by directory name. </summary>
    public bool DeleteOrphan(string modDirectory)
    {
        if (!penumbra.Available)
            return false;

        var ec = penumbra.DeleteMod(modDirectory);
        InvalidateCaches();
        if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged and not PenumbraApiEc.ModMissing)
        {
            DynamicTextureManager.Log.Warning($"Could not delete orphaned mod {modDirectory}: {ec}.");
            return false;
        }

        return true;
    }

    private void InvalidateCaches()
    {
        _stateCache     = null;
        _allStatesCache = null;
        _orphanCache    = null;
    }

    #endregion

    /// <summary>
    /// Enabled state of a dTexture's generated mod, from a bulk query cached for per-frame
    /// use (e.g. graying out disabled entries in the selector). Null when unknown or not built.
    /// </summary>
    public bool? IsModEnabled(DTexture dTexture)
    {
        var dir = dTexture.Data.OutputModDirectory;
        if (dir.Length == 0 || !penumbra.Available)
            return null;

        var states = GetAllEnabledStates();
        if (states == null)
            return null;

        return states.TryGetValue(dir, out var enabled) ? enabled : null;
    }

    private Dictionary<string, bool>? GetAllEnabledStates()
    {
        if (_allStatesCache is { } cache && Environment.TickCount64 - cache.FetchedMs < 1000)
            return cache.States;

        try
        {
            var (valid, _, collection) = penumbra.GetCollectionForObject(0);
            if (!valid)
                return null;

            var states = penumbra.GetAllModEnabledStates(collection.Id);
            if (states == null)
                return null;

            _allStatesCache = (states, Environment.TickCount64);
            return states;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not query mod enabled states: {ex.Message}");
            return null;
        }
    }

    /// <summary> Enabled state of a dTexture's generated mod in the player's collection, cached briefly for per-frame UI use. </summary>
    public (bool Enabled, string CollectionName)? QueryModState(DTexture dTexture)
    {
        var dir = dTexture.Data.OutputModDirectory;
        if (!penumbra.Available || dir.Length == 0)
            return null;

        if (_stateCache is { } cache && cache.Dir == dir && Environment.TickCount64 - cache.FetchedMs < 500)
            return (cache.Enabled, cache.CollectionName);

        try
        {
            var (valid, _, collection) = penumbra.GetCollectionForObject(0);
            if (!valid)
                return null;

            var settings = penumbra.GetModSettings(collection.Id, dir);
            if (settings == null)
                return null;

            _stateCache = (dir, collection.Id, collection.Name, settings.Value.Enabled, Environment.TickCount64);
            return (settings.Value.Enabled, collection.Name);
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not query mod state of {dir}: {ex.Message}");
            return null;
        }
    }

    /// <summary> Enable or disable a dTexture's generated mod in the player's collection. </summary>
    public bool SetModEnabled(DTexture dTexture, bool enabled)
    {
        var dir = dTexture.Data.OutputModDirectory;
        if (!penumbra.Available || dir.Length == 0)
            return false;

        try
        {
            var (valid, _, collection) = penumbra.GetCollectionForObject(0);
            if (!valid)
                return Fail("Could not determine your collection.");

            var ec = penumbra.TrySetMod(collection.Id, dir, enabled);
            _stateCache     = null;
            _allStatesCache = null;
            if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
                return Fail($"Could not {(enabled ? "enable" : "disable")} the mod: {ec}.");

            penumbra.RedrawObject(0);
            LastResult = $"{(enabled ? "Enabled" : "Disabled")} mod \"{dir}\" in collection \"{collection.Name}\".";
            return true;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not set mod state of {dir}: {ex.Message}");
            return false;
        }
    }

    public string ModDirectoryName(DTexture dTexture)
        => dTexture.Data.OutputModDirectory.Length > 0
            ? dTexture.Data.OutputModDirectory
            : $"DTM_{dTexture.Incognito}";

    private sealed record TextureJob(string GamePath, string? DiskPath, List<DTextures.Data.TextureLayer> Layers, MaterialMesh? Mesh)
    {
        /// <summary> Sibling-texture slot this job applies material effects for (normal/mask), if any. </summary>
        public Shaders.TextureSlot EffectSlot { get; init; } = Shaders.TextureSlot.Unknown;

        /// <summary> Decal layers from the material's other textures whose effects replay onto this one. </summary>
        public List<DTextures.Data.TextureLayer> EffectLayers { get; init; } = [];
    }

    private sealed record BuildPlan(Dictionary<string, byte[]> MaterialFiles, List<TextureJob> TextureJobs);

    /// <summary>
    /// Build the overlay mod for a dTexture and register or reload it in Penumbra.
    /// Source gathering happens on the calling (framework) thread; texture compositing and
    /// BC compression run in the background, then registration hops back to the framework.
    /// </summary>
    public bool Apply(DTexture dTexture)
    {
        if (Busy)
            return Fail("A build is already running.");
        if (!penumbra.Available)
            return Fail("Penumbra is not available.");
        if (dTexture.Data.Source.IsEmpty)
            return Fail("No source selected.");
        if (!dTexture.Data.HasEdits)
            return Fail("No edits to apply.");

        Busy = true;
        string    dirName, modDirectory;
        bool      isNew;
        BuildPlan plan;
        try
        {
            var modRoot = penumbra.GetModDirectory();
            if (modRoot.Length == 0 || !Directory.Exists(modRoot))
            {
                Busy = false;
                return Fail($"Penumbra mod directory \"{modRoot}\" does not exist.");
            }

            dirName      = ModDirectoryName(dTexture);
            modDirectory = Path.Combine(modRoot, dirName);
            isNew        = !Directory.Exists(modDirectory);

            plan = PrepareBuild(dTexture, modDirectory);
            if (plan.MaterialFiles.Count == 0 && plan.TextureJobs.Count == 0)
            {
                Busy = false;
                return Fail("No files could be built from the current edits.");
            }
        }
        catch (Exception ex)
        {
            Busy = false;
            DynamicTextureManager.Log.Error($"Failed to prepare build for dTexture {dTexture.Identifier}:\n{ex}");
            return Fail($"Build failed: {ex.Message}");
        }

        LastResult = plan.TextureJobs.Count > 0 ? "Building textures..." : "Building...";
        _ = Task.Run(async () =>
        {
            try
            {
                var written = await BuildAndWriteAsync(dTexture, modDirectory, plan).ConfigureAwait(false);
                await framework.RunOnFrameworkThread(() =>
                {
                    if (written == 0)
                    {
                        Fail("No files could be built from the current edits.");
                        return;
                    }

                    var (ec, statusDetail) = RegisterOrReload(dTexture, dirName, isNew);
                    if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
                    {
                        Fail($"Penumbra rejected the mod: {ec}.");
                        return;
                    }

                    dTexture.Data.OutputModDirectory = dirName;
                    saveService.QueueSave(dTexture);
                    InvalidateCaches();

                    penumbra.RedrawObject(0);
                    LastResult = $"Applied {written} file(s) as mod \"{dirName}\"{statusDetail}.";
                    DynamicTextureManager.Log.Information(LastResult);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Error($"Failed to apply dTexture {dTexture.Identifier}:\n{ex}");
                LastResult = $"Build failed: {ex.Message}";
            }
            finally
            {
                Busy = false;
            }
        });
        return true;
    }

    /// <summary> Gather all source inputs on the calling thread so the background build needs no further IPC. </summary>
    private BuildPlan PrepareBuild(DTexture dTexture, string modDirectory)
    {
        var materials = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, edit) in dTexture.Data.Materials.Where(kvp => !kvp.Value.IsEmpty))
        {
            var source = dTexture.Data.Source.Materials.FirstOrDefault(m
                => string.Equals(m.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                DynamicTextureManager.Log.Warning($"Material {gamePath} has edits but is not part of the source, skipped.");
                continue;
            }

            var mtrl = sourceFiles.GetMaterial(source, modDirectory);
            if (mtrl == null)
                continue;

            if (mtrl.Table is not ColorTable)
            {
                DynamicTextureManager.Log.Warning(
                    $"Material {gamePath} does not use a Dawntrail color table (shader {mtrl.ShaderPackage.Name}), skipped. Legacy tables are not supported yet.");
                continue;
            }

            if (MaterialEditApplier.Apply(mtrl, edit) == 0)
                continue;

            materials[gamePath] = mtrl.Write();
        }

        var textures = new List<TextureJob>();
        // A texture needs a job when any layer stamps onto it — or when an extraction
        // redirected its source to a cleaned copy: that base must ship even with every
        // layer disabled, otherwise the source mod's file (baked decal included) resolves
        // again and "disabled" would un-hide the extracted decal.
        foreach (var (gamePath, layers) in dTexture.Data.Textures.Where(kvp
                     => kvp.Value.Any(l => l.Enabled || l is DTextures.Data.DecalLayer { Extracted: true, PreExtractionSource: not null })))
        {
            // Always bake from the pristine source captured when the layer was added — a
            // build-time resolve would return our own generated file and compound the bake.
            var diskPath = GetOrCaptureTextureSource(dTexture, gamePath);

            // Surface-projected layers bake through the material's bind-pose mesh.
            MaterialMesh? mesh = null;
            if (layers.Any(l => l is DTextures.Data.DecalLayer { Surface: true, Enabled: true }))
            {
                var owner = CompositePlanner.FindTextureOwner(dTexture.Data, gamePath, shaderHandlers, sourceFiles);
                mesh = owner != null ? uvReader.GetMesh(owner) : null;
                if (mesh == null)
                    DynamicTextureManager.Log.Warning(
                        $"No mesh geometry for {gamePath} — surface decals on it will be skipped this build.");
            }

            textures.Add(new TextureJob(gamePath, diskPath is { Length: > 0 } ? diskPath : null, layers, mesh));
        }

        AddSiblingEffectJobs(dTexture, textures);

        return new BuildPlan(materials, textures);
    }

    /// <summary>
    /// All textures of a material are related: decals with material effects (normal
    /// smoothing, mask finish) replay their footprint onto the material's normal/mask
    /// textures, which usually have no layers of their own — synthesize jobs for them.
    /// The discovery itself is shared with the preview cache via <see cref="CompositePlanner"/>.
    /// </summary>
    private void AddSiblingEffectJobs(DTexture dTexture, List<TextureJob> textures)
    {
        var meshCache = new Dictionary<string, MaterialMesh?>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in CompositePlanner.SiblingEffectTargets(dTexture.Data, shaderHandlers, sourceFiles))
        {
            MaterialMesh? mesh = null;
            if (target.NeedsMesh)
            {
                if (!meshCache.TryGetValue(target.Owner.GamePath, out mesh))
                    meshCache[target.Owner.GamePath] = mesh = uvReader.GetMesh(target.Owner);
                if (mesh == null)
                    DynamicTextureManager.Log.Warning(
                        $"No mesh geometry for {target.Owner.GamePath} — surface decal material effects will be skipped this build.");
            }

            var existing = textures.FindIndex(j => string.Equals(j.GamePath, target.GamePath, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                var job = textures[existing];
                textures[existing] = job with
                {
                    EffectSlot = target.Slot,
                    EffectLayers = [.. job.EffectLayers, .. target.Layers],
                    Mesh = job.Mesh ?? mesh,
                };
                continue;
            }

            // Capture the sibling's pristine source BEFORE our own mod first claims its
            // resolution — later resolves would return our generated file and compound.
            var diskPath = GetOrCaptureTextureSource(dTexture, target.GamePath);
            textures.Add(new TextureJob(target.GamePath, diskPath is { Length: > 0 } ? diskPath : null, [], mesh)
            {
                EffectSlot   = target.Slot,
                EffectLayers = target.Layers,
            });
        }
    }

    /// <summary>
    /// The pristine source file of a layered texture: the stored capture, else a fresh
    /// resolve (rejecting our own generated mod), else a search through the source mods'
    /// own file lists — the recovery path when our mod already owns the resolution.
    /// Empty string means vanilla, null means unknown. Successful captures are persisted.
    /// </summary>
    public string? GetOrCaptureTextureSource(DTexture dTexture, string gamePath)
    {
        if (dTexture.Data.TextureSourcePaths.TryGetValue(gamePath, out var stored))
            return stored;

        if (!penumbra.Available)
            return null;

        string? found = null;
        try
        {
            var modRoot = penumbra.GetModDirectory();
            var ownMod  = Path.Combine(modRoot, ModDirectoryName(dTexture));

            var resolved = penumbra.ResolvePlayerPath(gamePath);
            if (string.Equals(resolved, gamePath, StringComparison.OrdinalIgnoreCase))
                found = string.Empty; // vanilla
            else if (!PathUtil.IsInside(resolved, ownMod))
                found = resolved;
            else
                foreach (var sourceMod in dTexture.Data.Source.Materials
                             .Select(m => m.ModDirectory)
                             .Where(m => m.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    found = ModFileLocator.Find(Path.Combine(modRoot, sourceMod), gamePath);
                    if (found != null)
                    {
                        DynamicTextureManager.Log.Information(
                            $"Recovered pristine source of {gamePath} from source mod {sourceMod}.");
                        break;
                    }
                }
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not capture source of texture {gamePath}: {ex.Message}");
            return null;
        }

        if (found == null)
        {
            DynamicTextureManager.Log.Warning(
                $"Texture {gamePath} has no recoverable source — our own mod owns its resolution and no source mod provides it. Falling back to vanilla.");
            return null;
        }

        dTexture.Data.TextureSourcePaths[gamePath] = found;
        saveService.QueueSave(dTexture);
        return found;
    }

    /// <summary> Background part of the build: decode, composite and BC-compress textures, then commit the folder. </summary>
    private async Task<int> BuildAndWriteAsync(DTexture dTexture, string modDirectory, BuildPlan plan)
    {
        using var build   = modWriter.StartBuild(modDirectory);
        var       written = 0;

        foreach (var (gamePath, bytes) in plan.MaterialFiles)
        {
            build.WriteFile(gamePath, bytes);
            ++written;
        }

        foreach (var job in plan.TextureJobs)
        {
            var decoded = textureIO.Load(job.GamePath, job.DiskPath, modDirectory);
            if (decoded == null)
                continue;

            var rgba = compositor.CompositeFull(decoded, job.Layers, job.EffectLayers, job.EffectSlot, job.Mesh);

            var outFile = build.PrepareFile(job.GamePath);
            await penumbra.ConvertTextureData(rgba, decoded.Width, outFile, TextureType.Bc7Tex).ConfigureAwait(false);
            ++written;
        }

        if (written > 0)
            build.Commit(ModName(dTexture), DynamicTextureManager.Version);

        return written;
    }

    /// <summary> Delete the generated mod of a dTexture from Penumbra and disk. </summary>
    public bool DeleteMod(DTexture dTexture)
    {
        if (dTexture.Data.OutputModDirectory.Length == 0)
            return true;
        if (!penumbra.Available)
            return Fail("Penumbra is not available.");

        var ec = penumbra.DeleteMod(dTexture.Data.OutputModDirectory);
        if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged and not PenumbraApiEc.ModMissing)
            return Fail($"Could not delete mod \"{dTexture.Data.OutputModDirectory}\": {ec}.");

        dTexture.Data.OutputModDirectory = string.Empty;
        dTexture.Data.LastBuiltHash      = string.Empty;
        saveService.QueueSave(dTexture);
        _stateCache     = null;
        _allStatesCache = null;
        LastResult      = "Deleted generated mod.";
        return true;
    }

    private (PenumbraApiEc Ec, string StatusDetail) RegisterOrReload(DTexture dTexture, string dirName, bool isNew)
    {
        PenumbraApiEc ec;
        if (isNew || dTexture.Data.OutputModDirectory.Length == 0)
        {
            ec = penumbra.AddMod(dirName);
            if (ec is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)
                penumbra.SetModPath(dirName, $"DynamicTextureManager/{ModName(dTexture)}");
        }
        else
        {
            ec = penumbra.ReloadMod(dirName);
            // The user may have deleted the mod in Penumbra since the last build.
            if (ec is PenumbraApiEc.ModMissing)
            {
                ec = penumbra.AddMod(dirName);
                if (ec is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)
                    penumbra.SetModPath(dirName, $"DynamicTextureManager/{ModName(dTexture)}");
            }
        }

        if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
            return (ec, string.Empty);

        // Re-ensure enabled state and priority on every apply — a failed or reverted
        // setting would otherwise leave the mod built but invisible forever.
        var (valid, _, collection) = penumbra.GetCollectionForObject(0);
        if (!valid)
            return (PenumbraApiEc.Success, " — could not determine your collection, enable it in Penumbra manually");

        var enableEc   = penumbra.TrySetMod(collection.Id, dirName, true);
        var priorityEc = penumbra.TrySetModPriority(collection.Id, dirName, config.OverlayPriority);
        if (enableEc is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
        {
            DynamicTextureManager.Log.Warning($"Could not enable mod {dirName} in collection {collection.Name}: {enableEc}.");
            return (PenumbraApiEc.Success, $" — but enabling it in collection \"{collection.Name}\" failed: {enableEc}");
        }

        if (priorityEc is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
            DynamicTextureManager.Log.Warning($"Could not set priority of mod {dirName}: {priorityEc}.");

        return (PenumbraApiEc.Success, $" — enabled in collection \"{collection.Name}\" (priority {config.OverlayPriority})");
    }

    private static string ModName(DTexture dTexture)
        => $"DTM - {dTexture.Name.Text}";

    private bool Fail(string message)
    {
        LastResult = message;
        DynamicTextureManager.Log.Warning($"Overlay mod build: {message}");
        return false;
    }
}
