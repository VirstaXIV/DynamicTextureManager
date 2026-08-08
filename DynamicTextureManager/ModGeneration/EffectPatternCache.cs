using System;
using System.IO;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Services;
using SixLabors.ImageSharp;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Effect pattern pixels for the live viewport effect and thumbnails, cached per
/// (pattern, library entry) — ViewportEffect compares the array by reference, so the same
/// selection must return the same instance. The viewport's effect sampler expects a
/// SQUARE pattern, so image-based sources (library import, the game's glitter texture)
/// are resampled to a square here; the UV mapping is identical either way (the shader
/// tiles 0..1 regardless of texel dimensions), the build ships the original data.
/// This is preview-side only: the build's own pattern loading is
/// OverlayModManager.LoadEffectImage, which ships the original (non-square) data — the
/// two stay separate on purpose, their output contracts differ.
/// </summary>
public sealed class EffectPatternCache(DecalLibrary decals, TextureIO textureIO)
{
    private (int Pattern, Guid LibraryId, byte[] Pixels, int Size) _cache = (-1, Guid.Empty, [], 0);

    public (byte[] Pixels, int Size) Get(AnimatedHairEdit edit)
    {
        if (_cache.Pattern == edit.Pattern
         && _cache.LibraryId == edit.EffectLibraryId
         && _cache.Pixels.Length > 0)
            return (_cache.Pixels, _cache.Size);

        byte[]? source = null;
        var sourceW = 0;
        var sourceH = 0;
        if (edit.EffectLibraryId != Guid.Empty)
            try
            {
                var file = decals.EffectFilePath(edit.EffectLibraryId);
                if (File.Exists(file))
                {
                    using var image = Image.Load<Rgba32>(file);
                    source  = new byte[image.Width * image.Height * 4];
                    sourceW = image.Width;
                    sourceH = image.Height;
                    image.CopyPixelDataTo(source);
                }
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Warning($"Could not load library effect pattern {edit.EffectLibraryId}: {ex.Message}");
            }
        else if ((AnimatedHairBuilder.HairEffectPattern)edit.Pattern is AnimatedHairBuilder.HairEffectPattern.DressGlitter
         && textureIO.Load(AnimatedHairBuilder.DressGlitterTexPath, null, null) is { } glitter)
        {
            source  = glitter.Rgba;
            sourceW = glitter.Width;
            sourceH = glitter.Height;
        }

        byte[] pixels;
        int    size;
        if (source != null)
        {
            size   = AnimatedHairBuilder.PatternSize;
            pixels = ResampleSquare(source, sourceW, sourceH, size);
        }
        else
        {
            var pattern = (AnimatedHairBuilder.HairEffectPattern)edit.Pattern;
            if (pattern is AnimatedHairBuilder.HairEffectPattern.DressGlitter)
                pattern = AnimatedHairBuilder.HairEffectPattern.Shimmer;
            size   = AnimatedHairBuilder.PatternDimension(pattern);
            pixels = AnimatedHairBuilder.GeneratePattern(pattern, size);
        }

        _cache = (edit.Pattern, edit.EffectLibraryId, pixels, size);
        return (pixels, size);
    }

    private static byte[] ResampleSquare(byte[] rgba, int width, int height, int size)
    {
        var result = new byte[size * size * 4];
        for (var y = 0; y < size; ++y)
        {
            var sy = Math.Min(height - 1, y * height / size);
            for (var x = 0; x < size; ++x)
            {
                var sx = Math.Min(width - 1, x * width / size);
                Array.Copy(rgba, (sy * width + sx) * 4, result, (y * size + x) * 4, 4);
            }
        }

        return result;
    }
}
