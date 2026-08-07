using System;
using System.IO;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using IService = Luna.IService;

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
                {
                    try
                    {
                        tex = dataManager.GameData.GetFileFromDisk<TexFile>(diskPath);
                    }
                    catch (Exception ex)
                    {
                        // Some mod .tex files carry malformed mip-offset tables (old tools wrote
                        // offsets for the wrong dimensions). The game renders them fine — it only
                        // needs mip 0 — but Lumina trusts the table and throws.
                        DynamicTextureManager.Log.Warning(
                            $"Lumina could not read \"{diskPath}\" ({ex.Message}) — retrying as mip-0 only (malformed mip table).");
                        var fallback = LoadTexMip0(diskPath);
                        if (fallback == null)
                            throw;

                        return fallback;
                    }
                }
                else
                {
                    return LoadImageFile(diskPath);
                }
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

    /// <summary>
    /// Decode only mip 0 of a .tex whose mip-offset table cannot be trusted: parse the fixed
    /// 80-byte header ourselves, compute the mip-0 payload size from the format, and hand a
    /// sane single-mip buffer to Lumina's decoder.
    /// </summary>
    private static DecodedTexture? LoadTexMip0(string diskPath)
    {
        var bytes = File.ReadAllBytes(diskPath);
        if (bytes.Length < 80)
            return null;

        var attribute = (TexFile.Attribute)BitConverter.ToUInt32(bytes, 0);
        var format    = (TexFile.TextureFormat)BitConverter.ToUInt32(bytes, 4);
        int width     = BitConverter.ToUInt16(bytes, 8);
        int height    = BitConverter.ToUInt16(bytes, 10);
        var offset    = BitConverter.ToUInt32(bytes, 28);
        if (width <= 0 || height <= 0 || offset < 80 || offset >= bytes.Length)
            return null;

        // Format nibbles encode bits-per-pixel; block-compressed families (0x3xxx DXT,
        // 0x6xxx BC4-7) pack 4x4 blocks of bpp/8*16 bytes.
        var bitsPerPixel = 1 << (((int)format >> 4) & 0xF);
        var isBlock      = ((int)format & 0xF000) is 0x3000 or 0x6000;
        var mipSize = isBlock
            ? (width + 3) / 4 * ((height + 3) / 4) * bitsPerPixel * 2
            : width * height * bitsPerPixel / 8;
        if (mipSize <= 0 || offset + mipSize > bytes.Length)
            return null;

        var payload = new byte[mipSize];
        Array.Copy(bytes, offset, payload, 0, mipSize);
        var buffer = Lumina.Data.Parsing.Tex.Buffers.TextureBuffer.FromTextureFormat(
            attribute, format, width, height, 1, [mipSize], payload, Lumina.Data.Structs.PlatformId.Win32);
        return new DecodedTexture(BgraToRgba(buffer.Filter(0, 0, TexFile.TextureFormat.B8G8R8A8).RawData), width, height);
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
