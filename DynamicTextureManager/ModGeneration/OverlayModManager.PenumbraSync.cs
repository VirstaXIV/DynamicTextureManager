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

// Penumbra-state synchronization: auto-apply queueing, reacting to mods deleted or
// renamed inside Penumbra, orphan discovery/cleanup, and the enable-state and
// priority queries the UI shows per canvas group.
public sealed partial class OverlayModManager
{
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
    private void OnDTextureChanged(in DTextureChanged.Arguments args)
    {
        var (type, dTexture, _) = args;
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

    /// <summary> The generated mod's Penumbra priority: the group's own value, else the global default. </summary>
    public int EffectivePriority(DTexture dTexture)
        => dTexture.Data.ModPriority ?? config.OverlayPriority;

    /// <summary> Store a new priority for the group's mod and push it to Penumbra when built. </summary>
    public void SetModPriority(DTexture dTexture, int? priority)
    {
        dTexture.Data.ModPriority = priority;
        saveService.QueueSave(dTexture);

        var dir = dTexture.Data.OutputModDirectory;
        if (!penumbra.Available || dir.Length == 0)
            return;

        try
        {
            var (valid, _, collection) = penumbra.GetCollectionForObject(0);
            if (!valid)
                return;

            var ec = penumbra.TrySetModPriority(collection.Id, dir, EffectivePriority(dTexture));
            if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
                DynamicTextureManager.Log.Warning($"Could not set priority of mod {dir}: {ec}.");
            else
                penumbra.RedrawObject(0);
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not set priority of mod {dir}: {ex.Message}");
        }
    }
}
