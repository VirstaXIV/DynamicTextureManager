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
    public byte[] Composite(DecodedTexture baseTexture, IEnumerable<TextureLayer> layers, MaterialMesh? mesh = null)
    {
        using var image = Image.LoadPixelData<Rgba32>(baseTexture.Rgba, baseTexture.Width, baseTexture.Height);

        foreach (var layer in layers)
        {
            if (!layer.Enabled)
                continue;

            switch (layer)
            {
                case DecalLayer decal:
                    ApplyDecal(image, decal, mesh);
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
    /// The full per-texture composite a build writes (minus BC7): own layers, then sibling
    /// material effects. The one sequence shared by the mod build and the preview cache, so
    /// previews stay pixel-identical to built files by construction.
    /// </summary>
    public byte[] CompositeFull(DecodedTexture baseTexture, IEnumerable<TextureLayer> layers,
        IReadOnlyList<TextureLayer> effectLayers, TextureSlot effectSlot, MaterialMesh? mesh)
    {
        var rgba = Composite(baseTexture, layers, mesh);
        if (effectLayers.Count > 0)
            rgba = CompositeSiblingEffects(new DecodedTexture(rgba, baseTexture.Width, baseTexture.Height),
                effectLayers, effectSlot, mesh);
        return rgba;
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

            ApplyDecal(image, layer, mesh, effectSlot: slot);
        }

        var result = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(result);
        return result;
    }

    /// <summary>
    /// The per-texel material effect for sibling textures: smooth the normal map toward flat
    /// (RG 128/128 is the neutral tangent normal) or write the finish into the mask map.
    /// Mask channel semantics live in <see cref="FinishMapping"/>.
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
            case TextureSlot.Mask when layer.WantsMaskEffect:
                FinishMapping.ApplyToMaskPixel(ref pixel, layer);
                return true;
            default:
                return false;
        }
    }

    /// <summary> Mirror the decal image in its own space, before any resize/rotation/projection. </summary>
    private static void ApplyFlips(Image<Rgba32> image, DecalLayer layer)
    {
        if (layer.FlipX)
            image.Mutate(c => c.Flip(FlipMode.Horizontal));
        if (layer.FlipY)
            image.Mutate(c => c.Flip(FlipMode.Vertical));
    }

    private static byte LerpByte(byte from, byte to, float t)
        => (byte)Math.Clamp((int)Math.Round(from + (to - from) * t), 0, 255);

    private void ApplyDecal(Image<Rgba32> target, DecalLayer layer, MaterialMesh? mesh, TextureSlot? effectSlot = null)
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
            ApplyFlips(source, layer);
            SurfaceDecalBaker.Bake(target, source, mesh, layer, effectSlot);
            return;
        }

        // Material effects can cover a larger or smaller area than the decal itself.
        var scale  = effectSlot != null ? Math.Max(0.01f, layer.EffectScale) : 1f;
        var width  = Math.Max(1, (int)Math.Round(layer.ScaleX * scale * target.Width));
        var height = Math.Max(1, (int)Math.Round(layer.ScaleY * scale * target.Height));

        using var decal = Image.Load<Rgba32>(path);
        ApplyFlips(decal, layer);
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
        else if (layer.HasTint)
            ApplyTintedDecal(target, decal, layer, x, y);
        else
            target.Mutate(c => c.DrawImage(decal, new Point(x, y), layer.Opacity));
    }

    /// <summary>
    /// Recolored diffuse decal: each pixel renders its tint color and alpha-blends into the
    /// target's RGB only — the target's alpha channel can carry material data (skin) and
    /// must survive the stamp. Soft edges stay soft; the alpha threshold gates only palette
    /// extraction, not blending.
    /// </summary>
    private static void ApplyTintedDecal(Image<Rgba32> target, Image<Rgba32> decal, DecalLayer layer, int offsetX, int offsetY)
    {
        var opacity = Math.Clamp(layer.Opacity, 0f, 1f);

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

                var sample = DecalQuantizer.ApplyTint(decal[dx, dy], layer);
                var alpha  = sample.A / 255f * opacity;
                if (alpha <= 0f)
                    continue;

                var pixel = target[tx, ty];
                pixel.R        = LerpByte(pixel.R, sample.R, alpha);
                pixel.G        = LerpByte(pixel.G, sample.G, alpha);
                pixel.B        = LerpByte(pixel.B, sample.B, alpha);
                target[tx, ty] = pixel;
            }
        }
    }

    /// <summary>
    /// Fill an extracted decal's original id-map footprint with the surrounding garment
    /// values: R gets the dominant neighboring pair, G the median neighboring blend — the
    /// closest stand-in for what the garment would render without the baked decal. Used to
    /// generate the cleaned source copy an extraction redirects its texture to.
    /// </summary>
    public static void EraseExtractedFootprint(Image<Rgba32> target, DecalLayer layer, string stampPath)
    {
        if (!File.Exists(stampPath))
        {
            DynamicTextureManager.Log.Warning($"Extracted decal {layer.DecalId} is missing from the library — its original footprint stays in place.");
            return;
        }

        var x0 = (int)Math.Round(layer.SourceU * target.Width);
        var y0 = (int)Math.Round(layer.SourceV * target.Height);
        var w  = Math.Max(1, (int)Math.Round(layer.SourceUW * target.Width));
        var h  = Math.Max(1, (int)Math.Round(layer.SourceUH * target.Height));

        using var stamp = Image.Load<Rgba32>(stampPath);
        if (stamp.Width != w || stamp.Height != h)
            stamp.Mutate(c => c.Resize(w, h, KnownResamplers.NearestNeighbor));

        var threshold = layer.AlphaThresholdByte;
        var fillR     = IdMapTexel.PairByte(layer.FillPair);

        for (var dy = 0; dy < h; ++dy)
        {
            var ty = y0 + dy;
            if (ty < 0 || ty >= target.Height)
                continue;

            for (var dx = 0; dx < w; ++dx)
            {
                var tx = x0 + dx;
                if (tx < 0 || tx >= target.Width || stamp[dx, dy].A < threshold)
                    continue;

                var pixel = target[tx, ty];
                pixel.R = fillR;
                if (layer.FillBlend >= 0)
                    pixel.G = (byte)Math.Clamp(layer.FillBlend, 0, 255);
                target[tx, ty] = pixel;
            }
        }
    }

    /// <summary> Replay a flat decal's footprint onto a sibling texture, applying its material effect per texel. </summary>
    private static void ApplyFlatEffect(Image<Rgba32> target, Image<Rgba32> decal, DecalLayer layer, int offsetX, int offsetY,
        TextureSlot slot)
    {
        var threshold = layer.AlphaThresholdByte;

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

        var threshold = layer.AlphaThresholdByte;

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

                var row   = layer.PaletteRows[DecalQuantizer.NearestIndex(source, layer.PaletteColors)];
                var pixel = target[tx, ty];
                IdMapTexel.StampRow(ref pixel, row, source.A, layer.WriteBlendFromAlpha);
                target[tx, ty] = pixel;
            }
        }
    }
}
