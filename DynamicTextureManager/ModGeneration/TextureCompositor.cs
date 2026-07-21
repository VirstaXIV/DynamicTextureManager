using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration.Shaders;
using DynamicTextureManager.Services;
using OtterGui.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DynamicTextureManager.ModGeneration;

/// <summary> Composites decal layers onto a base texture in RGBA space. </summary>
public sealed class TextureCompositor(DecalLibrary decals) : IService
{
    /// <summary>
    /// Apply all enabled layers onto the base texture. Returns the composited RGBA buffer.
    /// Surface-projected layers need the material's mesh geometry; without it they are skipped.
    /// </summary>
    public byte[] Composite(DecodedTexture baseTexture, IEnumerable<TextureLayer> layers, MaterialMesh? mesh = null,
        bool previewHighlight = false)
    {
        using var image = Image.LoadPixelData<Rgba32>(baseTexture.Rgba, baseTexture.Width, baseTexture.Height);

        foreach (var layer in layers)
        {
            if (!layer.Enabled)
                continue;

            switch (layer)
            {
                case DecalLayer decal:
                    ApplyDecal(image, decal, mesh, previewHighlight);
                    break;
                default:
                    DynamicTextureManager.Log.Warning($"Unknown layer type {layer.LayerType}, skipped.");
                    break;
            }
        }

        var result = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(result);
        return result;
    }

    /// <summary>
    /// Replay decal footprints onto a sibling texture of the same material (normal or mask
    /// map), applying each layer's material effect instead of its colors. Placement is fully
    /// UV-normalized, so resolution differences between the siblings do not matter.
    /// </summary>
    public byte[] CompositeSiblingEffects(DecodedTexture baseTexture, IEnumerable<TextureLayer> layers, TextureSlot slot,
        MaterialMesh? mesh = null)
    {
        using var image = Image.LoadPixelData<Rgba32>(baseTexture.Rgba, baseTexture.Width, baseTexture.Height);

        foreach (var layer in layers.OfType<DecalLayer>())
        {
            if (!layer.Enabled || !layer.HasMaterialEffects)
                continue;

            ApplyDecal(image, layer, mesh, previewHighlight: false, effectSlot: slot);
        }

        var result = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(result);
        return result;
    }

    /// <summary>
    /// The per-texel material effect for sibling textures: smooth the normal map toward flat
    /// (RG 128/128 is the neutral tangent normal) or write the mask finish preset. Mask
    /// channel semantics are empirical — G is treated as roughness on Dawntrail character
    /// masks; adjust here if in-game verification says otherwise.
    /// </summary>
    internal static bool ApplyEffectPixel(ref Rgba32 pixel, in Rgba32 sample, byte threshold, DecalLayer layer, TextureSlot slot)
    {
        if (sample.A < threshold)
            return false;

        switch (slot)
        {
            case TextureSlot.Normal when layer.NormalSmooth > 0f:
                pixel.R = LerpByte(pixel.R, 128, layer.NormalSmooth);
                pixel.G = LerpByte(pixel.G, 128, layer.NormalSmooth);
                return true;
            case TextureSlot.Mask when layer.MaskPreset != DecalMaskPreset.Keep:
                pixel.G = layer.MaskPreset == DecalMaskPreset.Matte ? (byte)200 : (byte)30;
                return true;
            default:
                return false;
        }
    }

    private static byte LerpByte(byte from, byte to, float t)
        => (byte)Math.Clamp((int)Math.Round(from + (to - from) * t), 0, 255);

    private void ApplyDecal(Image<Rgba32> target, DecalLayer layer, MaterialMesh? mesh, bool previewHighlight,
        TextureSlot? effectSlot = null)
    {
        var path = decals.FilePath(layer.DecalId);
        if (!File.Exists(path))
        {
            DynamicTextureManager.Log.Warning($"Decal {layer.DecalId} is missing from the library, layer skipped.");
            return;
        }

        if (layer.Surface)
        {
            if (mesh == null)
            {
                DynamicTextureManager.Log.Warning("Surface decal skipped — no mesh geometry available for this texture's material.");
                return;
            }

            using var source = Image.Load<Rgba32>(path);
            SurfaceDecalBaker.Bake(target, source, mesh, layer, previewHighlight, effectSlot);
            return;
        }

        // Material effects can cover a larger or smaller area than the decal itself.
        var scale  = effectSlot != null ? Math.Max(0.01f, layer.EffectScale) : 1f;
        var width  = Math.Max(1, (int)Math.Round(layer.ScaleX * scale * target.Width));
        var height = Math.Max(1, (int)Math.Round(layer.ScaleY * scale * target.Height));

        using var decal = Image.Load<Rgba32>(path);
        // Bilinear resampling invents blend colors at edges; keep colorset decals crisp so
        // every pixel nearest-maps to one of the extracted palette colors.
        if (layer.IdRemap)
            decal.Mutate(c => c.Resize(width, height, KnownResamplers.NearestNeighbor));
        else
            decal.Mutate(c => c.Resize(width, height));
        if (Math.Abs(layer.RotationDeg) > 0.01f)
            decal.Mutate(c => c.Rotate(layer.RotationDeg));

        // Rotation grows the canvas; center the (possibly rotated) decal on the target UV position.
        var x = (int)Math.Round(layer.PosU * target.Width - decal.Width / 2f);
        var y = (int)Math.Round(layer.PosV * target.Height - decal.Height / 2f);

        if (effectSlot is { } slot)
            ApplyFlatEffect(target, decal, layer, x, y, slot);
        else if (layer.IdRemap)
            ApplyIdRemap(target, decal, layer, x, y);
        else
            target.Mutate(c => c.DrawImage(decal, new Point(x, y), layer.Opacity));
    }

    /// <summary> Replay a flat decal's footprint onto a sibling texture, applying its material effect per texel. </summary>
    private static void ApplyFlatEffect(Image<Rgba32> target, Image<Rgba32> decal, DecalLayer layer, int offsetX, int offsetY,
        TextureSlot slot)
    {
        var threshold = (byte)Math.Clamp((int)Math.Round(layer.AlphaThreshold * 255f), 1, 255);

        for (var dy = 0; dy < decal.Height; ++dy)
        {
            var ty = offsetY + dy;
            if (ty < 0 || ty >= target.Height)
                continue;

            for (var dx = 0; dx < decal.Width; ++dx)
            {
                var tx = offsetX + dx;
                if (tx < 0 || tx >= target.Width)
                    continue;

                var pixel = target[tx, ty];
                if (ApplyEffectPixel(ref pixel, decal[dx, dy], threshold, layer, slot))
                    target[tx, ty] = pixel;
            }
        }
    }

    /// <summary>
    /// Colorset decal: each opaque decal pixel is nearest-mapped to the layer's extracted
    /// palette and its ID texel remapped to the claimed slot rendering that color by writing
    /// ONLY the R channel (pair index). G carries the garment's baked shading — it blends
    /// the slot's A row (the color) toward its B row (the darkened shade partner), so the
    /// cloth shading stays visible on the decal and edge interpolation only darkens.
    /// </summary>
    private static void ApplyIdRemap(Image<Rgba32> target, Image<Rgba32> decal, DecalLayer layer, int offsetX, int offsetY)
    {
        if (layer.PaletteRows.Count == 0 || layer.PaletteRows.Count != layer.PaletteColors.Count)
        {
            DynamicTextureManager.Log.Warning("Colorset decal has no allocated rows, layer skipped.");
            return;
        }

        var threshold = (byte)Math.Clamp((int)Math.Round(layer.AlphaThreshold * 255f), 1, 255);

        for (var dy = 0; dy < decal.Height; ++dy)
        {
            var ty = offsetY + dy;
            if (ty < 0 || ty >= target.Height)
                continue;

            for (var dx = 0; dx < decal.Width; ++dx)
            {
                var tx = offsetX + dx;
                if (tx < 0 || tx >= target.Width)
                    continue;

                var source = decal[dx, dy];
                if (source.A < threshold)
                    continue;

                var row        = layer.PaletteRows[DecalQuantizer.NearestIndex(source, layer.PaletteColors)];
                var pixel      = target[tx, ty];
                pixel.R        = (byte)(row / 2 * 17);
                target[tx, ty] = pixel;
            }
        }
    }
}
