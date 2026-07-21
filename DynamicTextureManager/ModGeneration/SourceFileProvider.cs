using System;
using System.IO;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Interop;
using OtterGui.Services;
using Penumbra.GameData.Files;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Acquires source file bytes for a game path: the recorded actual (possibly modded) file
/// when it still exists, a fresh Penumbra resolve otherwise, vanilla game data as last resort.
/// Files inside our own generated mod are never used as sources to avoid self-reference on rebuilds.
/// </summary>
public sealed class SourceFileProvider(IDataManager dataManager, PenumbraService penumbra) : IService
{
    public byte[]? GetFile(SourcePath source, string? excludeDirectory)
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

    /// <summary> A path is usable as source when it is a real file outside our own generated mod directory. </summary>
    private static bool IsUsable(string path, string? excludeDirectory)
    {
        if (path.Length == 0 || !Path.IsPathRooted(path))
            return false;

        if (excludeDirectory != null && PathUtil.IsInside(path, excludeDirectory))
            return false;

        return File.Exists(path);
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
