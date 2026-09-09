using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Interop;
using IService = Luna.IService;
using Penumbra.GameData.Files;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Reads model geometry for source materials: the UV layout so the texture preview can show
/// where UV islands end, and the full bind-pose mesh for surface-projected decals.
/// </summary>
public sealed partial class ModelUvReader : IService
{
    private readonly Dictionary<string, MaterialMesh?> _meshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UvLayout?>     _uvCache   = new(StringComparer.OrdinalIgnoreCase);

    private readonly IDataManager    dataManager;
    private readonly PenumbraService penumbra;

    public ModelUvReader(IDataManager dataManager, PenumbraService penumbra)
    {
        this.dataManager = dataManager;
        this.penumbra    = penumbra;
    }

    /// <summary> Resolve and read a game file: recorded actual path, then Penumbra resolution, then vanilla. </summary>
    private byte[]? LoadGameFile(string gamePath, string actualPath = "")
    {
        if (actualPath.Length > 0 && Path.IsPathRooted(actualPath) && File.Exists(actualPath))
            return File.ReadAllBytes(actualPath);

        var resolved = penumbra.ResolvePlayerPath(gamePath);
        if (resolved.Length > 0 && Path.IsPathRooted(resolved) && File.Exists(resolved))
            return File.ReadAllBytes(resolved);

        return dataManager.GetFile(gamePath)?.Data;
    }

    /// <summary> The diffuse texture game path a material paints, empty when unreadable. </summary>
    private string MaterialDiffusePath(string gamePath, string actualPath = "")
    {
        try
        {
            var bytes = LoadGameFile(gamePath, actualPath);
            if (bytes == null)
                return string.Empty;

            var mtrl = new MtrlFile(bytes);
            foreach (var sampler in mtrl.ShaderPackage.Samplers)
                if (sampler.SamplerId == ShpkFile.DiffuseSamplerId && sampler.TextureIndex < mtrl.Textures.Length)
                    return mtrl.Textures[sampler.TextureIndex].Path;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not read material {gamePath}: {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary> Full bind-pose geometry for a source material, cached; null when the model cannot be read. </summary>
    public MaterialMesh? GetMesh(SourcePath source)
    {
        // Body skin: load the whole SmallClothes body instead of whatever (gear) model the
        // material was found on — tattoos must be placeable anywhere on the body, and gear
        // models only embed the few skin patches they expose.
        if (IsBodySkinMaterial(source.GamePath) && GetBodyMesh(source) is { } bodyMesh)
            return bodyMesh;

        // Face skin: the model derives from the material path itself. Must run before the
        // generic branch — face sources travel inside the Body unit and record the body's
        // model path as their unit key, which is not their geometry.
        if (IsFaceSkinMaterial(source.GamePath) && GetFaceMesh(source) is { } faceMesh)
            return faceMesh;

        if (source.MdlGamePath.Length == 0)
            return null;

        var key = CacheKey(source);
        if (_meshCache.TryGetValue(key, out var cached))
            return cached;

        MaterialMesh? mesh = null;
        try
        {
            var bytes = LoadModelBytes(source);
            if (bytes == null)
                DynamicTextureManager.Log.Warning(
                    $"Could not load model {source.MdlGamePath} (file \"{source.MdlActualPath}\") for its geometry.");
            else
            {
                var fileName = Path.GetFileName(source.GamePath);
                mesh = MdlMeshReader.ReadMeshes([new MdlFile(bytes)],
                    material => material.EndsWith(fileName, StringComparison.OrdinalIgnoreCase), fileName, source.MdlGamePath);
            }

            if (mesh != null)
                DynamicTextureManager.Log.Information(
                    $"Geometry of {source.GamePath}: {mesh.VertexCount} vertices, {mesh.TriangleCount} triangles, {mesh.PartCount} parts.");
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not read geometry of {source.MdlGamePath}: {ex.Message}");
        }

        _meshCache[key] = mesh;
        return mesh;
    }

    /// <summary>
    /// Raw material names the source's model references (e.g. "/mt_c0201h0179_hir_a.mtrl") —
    /// how a multi-material hairstyle's OTHER materials are discovered without each having
    /// been added as a source. Empty when the model cannot be loaded.
    /// </summary>
    public IReadOnlyList<string> ModelMaterialNames(SourcePath source)
    {
        if (source.MdlGamePath.Length == 0)
            return [];

        try
        {
            var bytes = LoadModelBytes(source);
            return bytes == null ? [] : new MdlFile(bytes).Materials;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not read material names of {source.MdlGamePath}: {ex.Message}");
            return [];
        }
    }

    /// <summary> UV layout for a source material, cached; null when the model cannot be read. </summary>
    public UvLayout? Get(SourcePath source)
    {
        var key = CacheKey(source);
        if (_uvCache.TryGetValue(key, out var cached))
            return cached;

        var mesh   = GetMesh(source);
        var layout = mesh == null ? null : UvLayout.Build(mesh);
        _uvCache[key] = layout;
        return layout;
    }

    private static string CacheKey(SourcePath source)
        => $"{source.MdlActualPath}|{source.MdlGamePath}|{source.GamePath}";

    private byte[]? LoadModelBytes(SourcePath source)
    {
        if (source.MdlActualPath.Length > 0 && Path.IsPathRooted(source.MdlActualPath) && File.Exists(source.MdlActualPath))
        {
            DynamicTextureManager.Log.Debug($"Model {source.MdlGamePath}: loading stored file \"{source.MdlActualPath}\".");
            return File.ReadAllBytes(source.MdlActualPath);
        }

        // The stored snapshot can be absent or stale (resource trees do not always carry a
        // usable actual path for model nodes — 2026-07-29: a modded hair mdl came through as
        // its game path, silently rendering the VANILLA mesh under the mod's textures).
        // Resolve fresh through the collection like the body models do.
        var resolved = penumbra.ResolvePlayerPath(source.MdlGamePath);
        if (resolved.Length > 0 && Path.IsPathRooted(resolved) && File.Exists(resolved))
        {
            DynamicTextureManager.Log.Debug($"Model {source.MdlGamePath}: loading resolved file \"{resolved}\".");
            return File.ReadAllBytes(resolved);
        }

        DynamicTextureManager.Log.Debug($"Model {source.MdlGamePath}: loading vanilla game file.");
        return dataManager.GetFile(source.MdlGamePath)?.Data;
    }
}
