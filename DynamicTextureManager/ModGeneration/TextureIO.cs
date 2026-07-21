using System;
using System.IO;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using OtterGui.Services;

namespace DynamicTextureManager.ModGeneration;

public sealed record DecodedTexture(byte[] Rgba, int Width, int Height);

/// <summary> Decodes game textures (vanilla or modded .tex on disk) to raw RGBA. </summary>
public sealed class TextureIO(IDataManager dataManager) : IService
{
    /// <summary>
    /// Load and decode a texture: from the given disk path when usable, else from vanilla
    /// game data by game path. Returns null when neither works.
    /// </summary>
    public DecodedTexture? Load(string gamePath, string? diskPath, string? excludeDirectory)
    {
        try
        {
            TexFile? tex = null;
            if (diskPath != null
             && diskPath.Length > 0
             && Path.IsPathRooted(diskPath)
             && (excludeDirectory == null || !PathUtil.IsInside(diskPath, excludeDirectory))
             && File.Exists(diskPath))
            {
                if (diskPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                    tex = dataManager.GameData.GetFileFromDisk<TexFile>(diskPath);
                else
                    return LoadImageFile(diskPath);
            }

            tex ??= dataManager.GetFile<TexFile>(gamePath);
            if (tex == null)
            {
                DynamicTextureManager.Log.Warning($"Could not load texture {gamePath}.");
                return null;
            }

            return new DecodedTexture(BgraToRgba(tex.ImageData), tex.Header.Width, tex.Header.Height);
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Could not decode texture {gamePath}:\n{ex}");
            return null;
        }
    }

    /// <summary> Mods occasionally ship plain image files; decode them with ImageSharp. </summary>
    private static DecodedTexture? LoadImageFile(string path)
    {
        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(path);
        var bytes = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(bytes);
        return new DecodedTexture(bytes, image.Width, image.Height);
    }

    private static byte[] BgraToRgba(byte[] bgra)
    {
        var ret = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            ret[i]     = bgra[i + 2];
            ret[i + 1] = bgra[i + 1];
            ret[i + 2] = bgra[i];
            ret[i + 3] = bgra[i + 3];
        }

        return ret;
    }
}
