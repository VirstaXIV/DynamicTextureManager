using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Interop;
using IService = Luna.IService;
using Penumbra.GameData.Files;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Acquires source file bytes for a game path: the recorded actual (possibly modded) file
/// when it still exists, a fresh Penumbra resolve otherwise, vanilla game data as last resort.
/// Files inside our own generated mod are never used as sources to avoid self-reference on rebuilds.
/// </summary>
public sealed class SourceFileProvider(IDataManager dataManager, PenumbraService penumbra) : IService
{
    // One preview rebuild resolves and reads the same handful of materials several times
    // (sibling-target discovery, owner lookup, classification), each a Penumbra IPC round
    // trip plus disk reads. A short TTL collapses those; edits to the underlying mod files
    // show up within the TTL. Bytes are shared read-only — GetMaterial parses a fresh
    // MtrlFile per call, so callers can keep mutating their copy.
    private const long CacheTtlMs = 2000;

    private readonly Dictionary<(string Game, string Actual, string Mod, string? Exclude), (long AtMs, byte[]? Bytes)> _fileCache = [];

    public byte[]? GetFile(SourcePath source, string? excludeDirectory)
    {
        var key = (source.GamePath, source.ActualPath, source.ModDirectory, excludeDirectory);
        var now = Environment.TickCount64;
        lock (_fileCache)
        {
            if (_fileCache.TryGetValue(key, out var hit) && now - hit.AtMs < CacheTtlMs)
                return hit.Bytes;
        }

        var bytes = GetFileUncached(source, excludeDirectory);
        lock (_fileCache)
        {
            if (_fileCache.Count >= 64)
                _fileCache.Clear();
            _fileCache[key] = (now, bytes);
        }

        return bytes;
    }

    private byte[]? GetFileUncached(SourcePath source, string? excludeDirectory)
    {
        if (IsUsable(source.ActualPath, excludeDirectory))
            return TryRead(source.ActualPath);

        if (penumbra.Available)
        {
            try
            {
                var resolved = penumbra.ResolvePlayerPath(source.GamePath);
                if (!string.Equals(resolved, source.GamePath, StringComparison.OrdinalIgnoreCase)
                 && IsUsable(resolved, excludeDirectory))
                    return TryRead(resolved);
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Warning($"Could not resolve {source.GamePath} through Penumbra: {ex.Message}");
            }

            // Recovery: when the stored path is stale and the live resolve is excluded (our
            // own generated mod), look the file up in the recorded source mod directly.
            if (source.ModDirectory.Length > 0)
                try
                {
                    var inMod = ModFileLocator.Find(Path.Combine(penumbra.GetModDirectory(), source.ModDirectory), source.GamePath);
                    if (inMod != null && IsUsable(inMod, excludeDirectory))
                    {
                        DynamicTextureManager.Log.Information(
                            $"Recovered source of {source.GamePath} from mod {source.ModDirectory}.");
                        return TryRead(inMod);
                    }
                }
                catch (Exception ex)
                {
                    DynamicTextureManager.Log.Warning($"Could not search mod {source.ModDirectory} for {source.GamePath}: {ex.Message}");
                }
        }

        return dataManager.GetFile(source.GamePath)?.Data;
    }

    public MtrlFile? GetMaterial(SourcePath source, string? excludeDirectory)
    {
        var bytes = GetFile(source, excludeDirectory);
        if (bytes == null)
        {
            DynamicTextureManager.Log.Warning($"Could not read source material {source.GamePath}.");
            return null;
        }

        try
        {
            return new MtrlFile(bytes);
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Could not parse source material {source.GamePath}:\n{ex}");
            return null;
        }
    }

    /// <summary> A path is usable as source when it is a real file outside ANY generated overlay mod. </summary>
    private bool IsUsable(string path, string? excludeDirectory)
    {
        if (path.Length == 0 || !Path.IsPathRooted(path))
            return false;

        if (excludeDirectory != null && PathUtil.IsInside(path, excludeDirectory))
            return false;

        // Generated overlays are NEVER sources, no matter who asks — not just the explicitly
        // excluded one. A vanilla-based source (e.g. the tail) records no actual file, so
        // after a build the live resolve returns our own CONVERTED material; classifying
        // from it flips the whole UI into the wrong mode (observed 2026-08-02: the converted
        // tail listed as colorset gear with id-map controls and a gray untextured viewport).
        if (IsGeneratedModFile(path))
            return false;

        return File.Exists(path);
    }

    /// <summary>
    /// Whether a path points into a DTM-generated overlay mod, by the "DTM_" directory
    /// prefix under the Penumbra mod root (the same fallback heuristic
    /// OverlayModManager.IsGeneratedModFile uses; a mod RENAMED away from the prefix is
    /// handled there for texture captures — material classification accepts the narrow gap).
    /// </summary>
    private bool IsGeneratedModFile(string path)
    {
        try
        {
            var modRoot = penumbra.Available ? penumbra.GetModDirectory() : string.Empty;
            if (modRoot.Length == 0 || !PathUtil.IsInside(path, modRoot))
                return false;

            var firstSegment = Path.GetRelativePath(modRoot, path).Split('/', '\\')[0];
            return firstSegment.StartsWith("DTM_", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? TryRead(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not read {path}: {ex.Message}");
            return null;
        }
    }
}
