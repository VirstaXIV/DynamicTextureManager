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
    private readonly Interop.HairColorReader hairColors;

    public OverlayModManager(PenumbraService penumbra, SourceFileProvider sourceFiles, ModWriter modWriter, SaveService saveService,
        Configuration config, DTextureStorage storage, DTextureChanged dTextureChanged, IFramework framework, TextureIO textureIO,
        TextureCompositor compositor, Shaders.ShaderHandlerRegistry shaderHandlers, ModelUvReader uvReader,
        Interop.HairColorReader hairColors)
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

    /// <summary>
    /// One animated-highlight conversion: after the material's composited hair NORMAL and
    /// MASK are produced (their texture jobs run in the same build), the four companion
    /// textures of the characterscroll replacement material are derived from them and
    /// written. MaskGamePath is empty when the source material has no mask — the flat
    /// reference tile ships instead.
    /// </summary>
    private sealed record AnimatedHairJob(string MaterialGamePath, string NormalGamePath, string MaskGamePath,
        AnimatedHairBuilder.TexturePaths Paths, DTextures.Data.AnimatedHairEdit Edit);

    private sealed record BuildPlan(Dictionary<string, byte[]> MaterialFiles, List<TextureJob> TextureJobs,
        List<AnimatedHairJob> AnimatedJobs);

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

        Busy = true;
        string    dirName, modDirectory;
        bool      isNew, cleaning;
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

            // Removing the last decal or source material must clean the built mod too — its
            // old baked files keep applying otherwise. With nothing left to build, an EXISTING
            // mod gets an empty commit (the per-file commit deletes everything stale); without
            // a built mod there is nothing to clean and the request fails like before.
            var emptyReason = dTexture.Data.Source.IsEmpty ? "No source selected."
                : !dTexture.Data.HasEdits ? "No edits to apply."
                : null;
            plan = emptyReason == null
                ? PrepareBuild(dTexture, modDirectory)
                : new BuildPlan(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase), [], []);
            cleaning = plan.MaterialFiles.Count == 0 && plan.TextureJobs.Count == 0;
            if (cleaning && isNew)
            {
                Busy = false;
                return Fail(emptyReason ?? "No files could be built from the current edits.");
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
                var written = await BuildAndWriteAsync(dTexture, modDirectory, plan, commitWhenEmpty: cleaning).ConfigureAwait(false);
                await framework.RunOnFrameworkThread(() =>
                {
                    if (written == 0 && !cleaning)
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

                    // Redraw EVERYONE, not just the player: body skin textures are shared —
                    // any other actor using the same file (retainers, synced players) keeps
                    // the old texture referenced, and a cached resource never reloads while
                    // referenced. Redrawing only the player left every rebuild invisible.
                    penumbra.RedrawAll();
                    LastResult = cleaning
                        ? $"Cleared mod \"{dirName}\" — nothing left to apply, its old files were removed."
                        : $"Applied {written} file(s) as mod \"{dirName}\"{statusDetail}.";
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
                // Skin/legacy materials never carry row edits (their decals are texture-only);
                // only warn when actual colorset edits would be dropped.
                if (edit.Rows.Count > 0)
                    DynamicTextureManager.Log.Warning(
                        $"Material {gamePath} has colorset row edits but no Dawntrail color table (shader {mtrl.ShaderPackage.Name}) — colorset edits require one, skipped.");
                continue;
            }

            if (MaterialEditApplier.Apply(mtrl, edit) == 0)
                continue;

            materials[gamePath] = mtrl.Write();
        }

        // Layer stacks can outlive their material: removing a source while another material
        // fails to load skips pruning (deliberately — a transient failure must never delete
        // layers), leaving stacks nothing in the UI shows anymore. Those must never build,
        // or ghost decals from removed sources keep shipping invisibly. When any source
        // material cannot be enumerated the filter is skipped — incomplete data must not
        // drop legitimate stacks either.
        HashSet<string>? exposed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var source in dTexture.Data.Source.Materials)
        {
            var sourceMtrl = sourceFiles.GetMaterial(source, modDirectory);
            if (sourceMtrl == null)
            {
                exposed = null;
                break;
            }

            foreach (var info in shaderHandlers.For(sourceMtrl).ClassifyTextures(sourceMtrl))
                exposed?.Add(info.GamePath);
        }

        var textures = new List<TextureJob>();
        // A texture needs a job when any layer stamps onto it — or when an extraction
        // redirected its source to a cleaned copy: that base must ship even with every
        // layer disabled, otherwise the source mod's file (baked decal included) resolves
        // again and "disabled" would un-hide the extracted decal.
        foreach (var (gamePath, layers) in dTexture.Data.Textures.Where(kvp
                     => kvp.Value.Any(l => l.Enabled || l is DTextures.Data.DecalLayer { Extracted: true, PreExtractionSource: not null })))
        {
            if (exposed != null && !exposed.Contains(gamePath))
            {
                DynamicTextureManager.Log.Warning(
                    $"Texture {gamePath} has {layers.Count} layer(s) but no current source material exposes it — skipped (leftover from a removed source).");
                continue;
            }

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
                        $"No mesh geometry for {gamePath} — surface decals and UV-aware hair zones fall back this build.");
            }

            textures.Add(new TextureJob(gamePath, diskPath is { Length: > 0 } ? diskPath : null, layers, mesh));
        }

        AddSiblingEffectJobs(dTexture, textures);
        AddOverlayCompanionJobs(dTexture, textures);

        var animated = PrepareAnimatedHair(dTexture, modDirectory, materials, textures);

        // One compact line of what actually builds — the first thing to check when a decal
        // ships that the UI no longer shows.
        DynamicTextureManager.Log.Debug(
            $"Build plan: {materials.Count} material(s); {animated.Count} animated hair conversion(s); "
          + $"textures [{string.Join(", ", textures.Select(t => $"{t.GamePath} ({t.Layers.Count} layer(s))"))}]");

        return new BuildPlan(materials, textures, animated);
    }

    /// <summary>
    /// Stage every enabled animated-highlight conversion: emit the characterscroll
    /// replacement material now (its structure only depends on the edit) and make sure the
    /// hair NORMAL has a texture job this build — the companion textures derive from its
    /// composited result, so all highlight edits still shape where the effect appears.
    /// </summary>
    private List<AnimatedHairJob> PrepareAnimatedHair(DTexture dTexture, string modDirectory,
        Dictionary<string, byte[]> materials, List<TextureJob> textures)
    {
        var animated  = new List<AnimatedHairJob>();
        var converted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void EnsureTextureJob(string gamePath)
        {
            // The composited texture must exist in this build even when it has no layers.
            if (textures.Any(t => string.Equals(t.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)))
                return;

            var layers   = dTexture.Data.Textures.GetValueOrDefault(gamePath) ?? [];
            var diskPath = GetOrCaptureTextureSource(dTexture, gamePath);
            MaterialMesh? mesh = null;
            if (layers.Any(l => l is DTextures.Data.DecalLayer { Surface: true, Enabled: true }))
            {
                var owner = CompositePlanner.FindTextureOwner(dTexture.Data, gamePath, shaderHandlers, sourceFiles);
                mesh = owner != null ? uvReader.GetMesh(owner) : null;
            }

            textures.Add(new TextureJob(gamePath, diskPath is { Length: > 0 } ? diskPath : null, layers, mesh));
        }

        bool Convert(string gamePath, Penumbra.GameData.Files.MtrlFile mtrl, DTextures.Data.AnimatedHairEdit edit)
        {
            var classified = shaderHandlers.For(mtrl).ClassifyTextures(mtrl).ToList();
            var normalPath = classified.FirstOrDefault(t => t.Slot == Shaders.TextureSlot.Normal).GamePath;
            if (string.IsNullOrEmpty(normalPath))
            {
                DynamicTextureManager.Log.Warning($"Animated hair for {gamePath} skipped — no normal texture on the material.");
                return false;
            }

            // The companion mask derives from the hair's own mask so per-strand shading
            // survives the conversion (shine edits included — they layer onto this texture).
            var maskPath = classified.FirstOrDefault(t => t.Slot == Shaders.TextureSlot.Mask).GamePath ?? string.Empty;

            var paths = AnimatedHairBuilder.PathsFor(gamePath);
            materials[gamePath] = AnimatedHairBuilder.BuildMaterial(mtrl, edit, paths);
            EnsureTextureJob(normalPath);
            if (maskPath.Length > 0)
                EnsureTextureJob(maskPath);

            animated.Add(new AnimatedHairJob(gamePath, normalPath, maskPath, paths, edit));
            converted.Add(gamePath);
            return true;
        }

        foreach (var (gamePath, storedEdit) in dTexture.Data.AnimatedHair.Where(kvp => kvp.Value.Enabled))
        {
            if (converted.Contains(gamePath))
                continue;

            // Hair + highlight colors follow the CHARACTER (Glamourer included) unless the
            // override toggle is set: resolve the live colors into the baked copy at build
            // time, squared to the colorset domain. Unreadable character -> the stored
            // fallback bakes instead. The effect color is always the stored one.
            var edit = storedEdit;
            if (!edit.OverrideHairColors)
            {
                if (hairColors.TryGetLocalPlayerHair(out var live))
                {
                    edit           = edit.Clone();
                    edit.BaseColor = [live.Main.X * live.Main.X, live.Main.Y * live.Main.Y, live.Main.Z * live.Main.Z];
                    edit.HighlightColor =
                    [
                        live.Highlight.X * live.Highlight.X, live.Highlight.Y * live.Highlight.Y,
                        live.Highlight.Z * live.Highlight.Z,
                    ];
                }
                else
                {
                    DynamicTextureManager.Log.Warning(
                        "Animated hair: character colors unreadable — baking the stored fallback colors.");
                }
            }

            var source = dTexture.Data.Source.Materials.FirstOrDefault(m
                => string.Equals(m.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                DynamicTextureManager.Log.Warning($"Animated hair for {gamePath} skipped — material is not part of the source.");
                continue;
            }

            var mtrl = sourceFiles.GetMaterial(source, modDirectory);
            if (mtrl == null || !Convert(gamePath, mtrl, edit))
                continue;

            // Multi-material hairstyles: the MODEL references every material of the style —
            // convert them all, whether or not each was ever added as a source. A partial
            // conversion leaves whole meshes on the plain hair shader.
            foreach (var rawName in uvReader.ModelMaterialNames(source))
            {
                var fileName    = Path.GetFileName(rawName);
                var siblingPath = AnimatedHairBuilder.SiblingMaterialGamePath(gamePath, fileName);
                if (siblingPath == null || converted.Contains(siblingPath))
                    continue;

                var siblingMtrl = sourceFiles.GetMaterial(new DTextures.Data.SourcePath { GamePath = siblingPath }, modDirectory);
                if (siblingMtrl == null)
                {
                    DynamicTextureManager.Log.Warning($"Animated hair sibling {siblingPath} could not be loaded, skipped.");
                    continue;
                }

                if (shaderHandlers.For(siblingMtrl).Kind(siblingMtrl) is not Shaders.MaterialKind.Hair)
                {
                    DynamicTextureManager.Log.Debug(
                        $"Model material {fileName} is not a hair-shader material — left unconverted.");
                    continue;
                }

                if (Convert(siblingPath, siblingMtrl, edit))
                    DynamicTextureManager.Log.Information(
                        $"Animated hair: also converting hairstyle sibling {siblingPath} (referenced by {source.MdlGamePath}).");
            }
        }

        return animated;
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
    /// Overlay-part textures (nails, accents — added as their own source materials) an enabled
    /// body-skin surface decal's footprint overlaps: the SAME layer reprojects onto the
    /// overlay's own mesh through the normal decal-application path (not a material-effect
    /// replay — this makes the tattoo itself appear there, in full color), so it continues
    /// seamlessly across the seam. One source of truth — no separate decal layers to keep in
    /// sync when the user edits or moves the original. Discovery shared with the preview cache
    /// via <see cref="CompositePlanner"/>, so the viewport shows the same result.
    /// </summary>
    private void AddOverlayCompanionJobs(DTexture dTexture, List<TextureJob> textures)
    {
        foreach (var target in CompositePlanner.OverlayCompanionTargets(dTexture.Data, shaderHandlers, sourceFiles, uvReader))
        {
            var mesh = uvReader.GetMesh(target.Owner);
            if (mesh == null)
            {
                DynamicTextureManager.Log.Warning(
                    $"No mesh geometry for {target.Owner.GamePath} — a body tattoo overlapping it will be skipped this build.");
                continue;
            }

            var existing = textures.FindIndex(j => string.Equals(j.GamePath, target.GamePath, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                var job = textures[existing];
                textures[existing] = job with { Layers = [.. job.Layers, .. target.Layers], Mesh = job.Mesh ?? mesh };
                continue;
            }

            var diskPath = GetOrCaptureTextureSource(dTexture, target.GamePath);
            textures.Add(new TextureJob(target.GamePath, diskPath is { Length: > 0 } ? diskPath : null, [.. target.Layers], mesh));
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
        {
            // A capture pointing into ANY generated overlay is poisoned — a remove/re-add
            // race can capture while an overlay still owns the resolution. Recapture instead
            // of baking generated output back in as "pristine". A capture whose file is gone
            // (source mod updated or removed) must recapture too — decoding would silently
            // fall back to vanilla and downgrade a hi-res base.
            if (!IsGeneratedModFile(stored) && (stored.Length == 0 || File.Exists(stored)))
                return stored;

            DynamicTextureManager.Log.Warning(
                $"Texture source of {gamePath} {(IsGeneratedModFile(stored) ? "pointed into a generated mod" : "no longer exists")} (\"{stored}\") — dropping it and recapturing.");
            dTexture.Data.TextureSourcePaths.Remove(gamePath);
        }

        if (!penumbra.Available)
            return null;

        string? found = null;
        try
        {
            var modRoot = penumbra.GetModDirectory();

            var resolved = penumbra.ResolvePlayerPath(gamePath);
            if (string.Equals(resolved, gamePath, StringComparison.OrdinalIgnoreCase))
                found = string.Empty; // vanilla
            else if (!IsGeneratedModFile(resolved))
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

            DynamicTextureManager.Log.Debug(
                $"Captured texture source of {gamePath}: {(found == null ? "(none)" : found.Length == 0 ? "(vanilla)" : $"\"{found}\"")}.");
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

    /// <summary>
    /// Whether a disk path points into any of our own generated overlay mods — never a
    /// pristine source. Checked two ways: by directory identity against every dTexture's
    /// currently tracked <see cref="DTextures.DTextureData.OutputModDirectory"/> (renames are
    /// tracked via <see cref="OnPenumbraModMoved"/>, so this catches a mod the user or Penumbra
    /// renamed away from the "DTM_" prefix — a real poisoning vector: a rename made the name
    /// check below blind to the mod, so its own baked output got captured and persisted as
    /// "pristine" forever, surviving even a removed/re-added decal), and by the "DTM_" name
    /// prefix as a fallback for mods not yet tracked (e.g. mid-build, or another dTexture this
    /// session hasn't loaded from storage).
    /// </summary>
    private bool IsGeneratedModFile(string path)
    {
        if (path.Length == 0 || !Path.IsPathRooted(path))
            return false;

        try
        {
            var modRoot = penumbra.GetModDirectory();
            if (modRoot.Length == 0 || !PathUtil.IsInside(path, modRoot))
                return false;

            foreach (var dTexture in storage)
            {
                var dir = dTexture.Data.OutputModDirectory;
                if (dir.Length > 0 && PathUtil.IsInside(path, Path.Combine(modRoot, dir)))
                    return true;
            }

            var firstSegment = Path.GetRelativePath(modRoot, path).Split('/', '\\')[0];
            return firstSegment.StartsWith("DTM_", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Background part of the build: decode, composite and BC-compress textures, then commit
    /// the folder. <paramref name="commitWhenEmpty"/> commits a deliberately empty build (a
    /// cleanup that deletes the mod's stale files); an ACCIDENTALLY empty result — every job
    /// failed to decode — must never commit, or it would wipe a previously good mod.
    /// </summary>
    private async Task<int> BuildAndWriteAsync(DTexture dTexture, string modDirectory, BuildPlan plan, bool commitWhenEmpty = false)
    {
        using var build   = modWriter.StartBuild(modDirectory);
        var       written = 0;

        foreach (var (gamePath, bytes) in plan.MaterialFiles)
        {
            build.WriteFile(gamePath, bytes);
            ++written;
        }

        // Composited normals + masks the animated-hair conversions derive their companions from.
        var animatedInputs = new Dictionary<string, (byte[] Rgba, int Width)>(StringComparer.OrdinalIgnoreCase);

        foreach (var job in plan.TextureJobs)
        {
            var decoded = textureIO.Load(job.GamePath, job.DiskPath, modDirectory);
            if (decoded == null)
                continue;

            DynamicTextureManager.Log.Debug($"Building {job.GamePath} at {decoded.Width}x{decoded.Height} (source {(job.DiskPath == null ? "vanilla" : $"\"{job.DiskPath}\"")}).");
            var rgba = compositor.CompositeFull(decoded, job.Layers, job.EffectLayers, job.EffectSlot, job.Mesh);

            if (plan.AnimatedJobs.Any(a => string.Equals(a.NormalGamePath, job.GamePath, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(a.MaskGamePath, job.GamePath, StringComparison.OrdinalIgnoreCase)))
                animatedInputs[job.GamePath] = (rgba, decoded.Width);

            var outFile = build.PrepareFile(job.GamePath);
            await penumbra.ConvertTextureData(rgba, decoded.Width, outFile, TextureType.Bc7Tex).ConfigureAwait(false);
            ++written;
        }

        foreach (var job in plan.AnimatedJobs)
        {
            if (!animatedInputs.TryGetValue(job.NormalGamePath, out var normal))
            {
                DynamicTextureManager.Log.Warning(
                    $"Animated hair companions for {job.MaterialGamePath} skipped — its normal {job.NormalGamePath} did not build.");
                continue;
            }

            // The id map carries exact colorset routing bytes and the reference keeps these
            // uncompressed — only the strand-detail normal and mask take BC7.
            await penumbra.ConvertTextureData(AnimatedHairBuilder.BuildNormalRgba(normal.Rgba), normal.Width,
                build.PrepareFile(job.Paths.Normal), TextureType.Bc7Tex).ConfigureAwait(false);
            await penumbra.ConvertTextureData(AnimatedHairBuilder.BuildIdRgba(normal.Rgba), normal.Width,
                build.PrepareFile(job.Paths.Id), TextureType.RgbaTex).ConfigureAwait(false);

            // Real per-strand shading: mask derived from the composited hair mask; the flat
            // white reference tile only when the material has no mask or it failed to build.
            if (job.MaskGamePath.Length > 0 && animatedInputs.TryGetValue(job.MaskGamePath, out var mask))
                await penumbra.ConvertTextureData(AnimatedHairBuilder.BuildCharMaskRgba(mask.Rgba), mask.Width,
                    build.PrepareFile(job.Paths.Mask), TextureType.Bc7Tex).ConfigureAwait(false);
            else
                await penumbra.ConvertTextureData(AnimatedHairBuilder.BuildMaskRgba(), AnimatedHairBuilder.MaskSize,
                    build.PrepareFile(job.Paths.Mask), TextureType.RgbaTex).ConfigureAwait(false);

            var (effect, effectWidth) = LoadEffectImage(job.Edit);
            await penumbra.ConvertTextureData(effect, effectWidth,
                build.PrepareFile(job.Paths.Effect), TextureType.RgbaTex).ConfigureAwait(false);

            written += 4;
            DynamicTextureManager.Log.Debug($"Animated hair companions written for {job.MaterialGamePath}.");
        }

        if (written > 0 || commitWhenEmpty)
            build.Commit(ModName(dTexture), DynamicTextureManager.Version);

        return written;
    }

    /// <summary>
    /// The black/white pattern the effect scrolls: the selected built-in pattern, or a legacy
    /// custom image when a save still carries one (the material references the effect texture
    /// unconditionally, so something always ships).
    /// </summary>
    private static (byte[] Rgba, int Width) LoadEffectImage(DTextures.Data.AnimatedHairEdit edit)
    {
        if (edit.EffectImagePath.Length > 0)
            try
            {
                using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(edit.EffectImagePath);
                var pixels = new byte[image.Width * image.Height * 4];
                image.CopyPixelDataTo(pixels);
                return (pixels, image.Width);
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Warning($"Could not load effect image \"{edit.EffectImagePath}\" ({ex.Message}) — using the built-in pattern.");
            }

        return (AnimatedHairBuilder.GeneratePattern((AnimatedHairBuilder.HairEffectPattern)edit.Pattern),
            AnimatedHairBuilder.PatternSize);
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
