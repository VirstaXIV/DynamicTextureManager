using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration.Shaders;
using DynamicTextureManager.Services;
using IService = Luna.IService;
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
    private byte[] Composite(DecodedTexture baseTexture, IEnumerable<TextureLayer> layers, MaterialMesh? mesh,
        System.Numerics.Vector3? skinTone)
    {
        if (!layers.Any(l => l.Enabled))
            return (byte[])baseTexture.Rgba.Clone();

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
                case HairShineLayer shine:
                    HairAdjust.ApplyShine(image, shine);
                    break;
                case ProceduralSurfaceLayer proc:
                    if (mesh != null)
                        ProceduralSurfaceBaker.Bake(image, mesh, proc, skinTone: skinTone);
                    else
                        DynamicTextureManager.Log.Warning("Procedural surface layer skipped — no mesh geometry available for this texture's material.");
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
        IReadOnlyList<TextureLayer> effectLayers, TextureSlot effectSlot, MaterialMesh? mesh,
        System.Numerics.Vector3? skinTone = null)
    {
        var rgba = Composite(baseTexture, layers, mesh, skinTone);
        if (effectLayers.Count > 0)
            rgba = CompositeSiblingEffects(new DecodedTexture(rgba, baseTexture.Width, baseTexture.Height),
                effectLayers, effectSlot, mesh, skinTone);
        return rgba;
    }

    /// <summary>
    /// Replay decal footprints onto a sibling texture of the same material (normal or mask
    /// map), applying each layer's material effect instead of its colors. Placement is fully
    /// UV-normalized, so resolution differences between the siblings do not matter.
    /// </summary>
    private byte[] CompositeSiblingEffects(DecodedTexture baseTexture, IEnumerable<TextureLayer> layers, TextureSlot slot,
        MaterialMesh? mesh, System.Numerics.Vector3? skinTone)
    {
        if (!layers.Any(l => l.Enabled && l.HasSiblingEffects))
            return baseTexture.Rgba;

        using var image = Image.LoadPixelData<Rgba32>(baseTexture.Rgba, baseTexture.Width, baseTexture.Height);

        foreach (var layer in layers)
        {
            if (!layer.Enabled || !layer.HasSiblingEffects)
                continue;

            switch (layer)
            {
                case DecalLayer decal:
                    ApplyDecal(image, decal, mesh, effectSlot: slot);
                    break;
                case ProceduralSurfaceLayer proc when mesh != null:
                    ProceduralSurfaceBaker.Bake(image, mesh, proc, effectSlot: slot, skinTone: skinTone);
                    break;
            }
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

    private sealed record CachedDecal(long Stamp, Rgba32[] Pixels, int Width, int Height);

    private readonly Dictionary<string, CachedDecal> _decalCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decal image decode, cached by file stamp — every composite stamps the same few PNGs,
    /// and effect replays load each one a second time per pass. The cached pixels are copied
    /// into a fresh image, so callers can keep mutating (flip/resize/rotate) their copy.
    /// </summary>
    private Image<Rgba32> LoadDecal(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path).Ticks;
        CachedDecal? cached;
        lock (_decalCache)
            _decalCache.TryGetValue(path, out cached);

        if (cached == null || cached.Stamp != stamp)
        {
            using var image = Image.Load<Rgba32>(path);
            var pixels = new Rgba32[image.Width * image.Height];
            image.CopyPixelDataTo(pixels);
            cached = new CachedDecal(stamp, pixels, image.Width, image.Height);
            lock (_decalCache)
            {
                if (_decalCache.Count >= 8 && !_decalCache.ContainsKey(path))
                    _decalCache.Clear();
                _decalCache[path] = cached;
            }
        }

        return Image.LoadPixelData<Rgba32>(cached.Pixels, cached.Width, cached.Height);
    }

    private void ApplyDecal(Image<Rgba32> target, DecalLayer layer, MaterialMesh? mesh, TextureSlot? effectSlot = null)
    {
        var path = decals.LayerImagePath(layer);
        if (!File.Exists(path))
        {
            DynamicTextureManager.Log.Warning($"Decal image {path} is missing, layer skipped.");
            return;
        }

        if (layer.Surface)
        {
            if (mesh == null)
            {
                DynamicTextureManager.Log.Warning("Surface decal skipped — no mesh geometry available for this texture's material.");
                return;
            }

            using var source = LoadDecal(path);
            ApplyFlips(source, layer);
            SurfaceDecalBaker.Bake(target, source, mesh, layer, effectSlot);
            return;
        }

        // Material effects can cover a larger or smaller area than the decal itself.
        var scale  = effectSlot != null ? Math.Max(0.01f, layer.EffectScale) : 1f;
        var width  = Math.Max(1, (int)Math.Round(layer.ScaleX * scale * target.Width));
        var height = Math.Max(1, (int)Math.Round(layer.ScaleY * scale * target.Height));

        using var decal = LoadDecal(path);
        ApplyFlips(decal, layer);
        var sourceWidth = decal.Width;
        // Bilinear resampling invents blend colors at edges; keep colorset decals crisp so
        // every pixel nearest-maps to one of the extracted palette colors.
        if (layer.IdRemap)
            decal.Mutate(c => c.Resize(width, height, KnownResamplers.NearestNeighbor));
        else
            decal.Mutate(c => c.Resize(width, height));

        // A heavy shrink area-averages sub-texel detail into faint smears; put the contrast
        // back the way an artist redrawing at the smaller size would (see ImageFilters).
        if (!layer.IdRemap && sourceWidth > 2 * decal.Width)
        {
            var pixels = new Rgba32[decal.Width * decal.Height];
            decal.CopyPixelDataTo(pixels);
            ImageFilters.SharpenPremultiplied(pixels, decal.Width, decal.Height, 0.6f);
            decal.ProcessPixelRows(accessor =>
            {
                for (var row = 0; row < accessor.Height; ++row)
                    pixels.AsSpan(row * accessor.Width, accessor.Width).CopyTo(accessor.GetRowSpan(row));
            });
        }

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
            ApplyColorDecal(target, decal, layer, x, y);
    }

    /// <summary>
    /// Diffuse decal stamp, recolored or plain: each pixel renders its (possibly tinted)
    /// color and alpha-blends into the target's RGB only — the target's alpha channel can
    /// carry material data (skin) and must survive the stamp, which rules out DrawImage
    /// (it composites alpha too). Soft edges stay soft; the alpha threshold gates only
    /// palette extraction, not blending.
    /// </summary>
    private static void ApplyColorDecal(Image<Rgba32> target, Image<Rgba32> decal, DecalLayer layer, int offsetX, int offsetY)
    {
        var opacity = Math.Clamp(layer.Opacity, 0f, 1f);
        if (opacity <= 0f)
            return;

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
                if (source.A == 0)
                    continue;

                var sample = DecalQuantizer.ApplyTint(source, layer);
                var alpha  = sample.A / 255f * opacity;
                if (alpha <= 0f)
                    continue;

                var pixel = target[tx, ty];
                pixel.R = LerpByte(pixel.R, sample.R, alpha);
                pixel.G = LerpByte(pixel.G, sample.G, alpha);
                pixel.B = LerpByte(pixel.B, sample.B, alpha);
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
            DynamicTextureManager.Log.Warning($"Extracted decal stamp {stampPath} is missing — its original footprint stays in place.");
            return;
        }

        var x0 = (int)Math.Round(layer.SourceU * target.Width);
        var y0 = (int)Math.Round(layer.SourceV * target.Height);
        var w  = Math.Max(1, (int)Math.Round(layer.SourceUW * target.Width));
        var h  = Math.Max(1, (int)Math.Round(layer.SourceUH * target.Height));

        using var stamp = Image.Load<Rgba32>(stampPath);
        if (stamp.Width != w || stamp.Height != h)
            stamp.Mutate(c => c.Resize(w, h, KnownResamplers.NearestNeighbor));

        var fillR = IdMapTexel.PairByte(layer.FillPair);

        for (var dy = 0; dy < h; ++dy)
        {
            var ty = y0 + dy;
            if (ty < 0 || ty >= target.Height)
                continue;

            for (var dx = 0; dx < w; ++dx)
            {
                var tx = x0 + dx;
                // Every marked texel was decal (content OR the alpha-1 erase halo) — the
                // layer's threshold only shapes the RESTAMP; erasing less than the whole
                // footprint leaves the original decal's fringe speckled across the map.
                if (tx < 0 || tx >= target.Width || stamp[dx, dy].A == 0)
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
    /// palette and its ID texel remapped to the claimed slot rendering that color. Solo
    /// slots write ONLY the R channel (pair index) — G carries the garment's baked shading,
    /// blending the slot's A row (the color) toward its B row (the darkened shade partner),
    /// so the cloth shading stays visible on the decal and edge interpolation only darkens.
    /// Gradient pairs (two blend-compatible colors sharing one pair) write G too: the
    /// pixel's own position between the pair's colors, preserving the decal's gradient and
    /// anti-aliasing detail.
    /// </summary>
    private static void ApplyIdRemap(Image<Rgba32> target, Image<Rgba32> decal, DecalLayer layer, int offsetX, int offsetY)
    {
        if (layer.PaletteRows.Count == 0 || layer.PaletteRows.Count != layer.PaletteColors.Count)
        {
            DynamicTextureManager.Log.Warning("Colorset decal has no allocated rows, layer skipped.");
            return;
        }

        var threshold = layer.AlphaThresholdByte;
        var partners  = DecalQuantizer.GradientPartners(layer);

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

                var index = DecalQuantizer.NearestIndex(source, layer.PaletteColors);
                var row   = layer.PaletteRows[index];
                var pixel = target[tx, ty];
                if (partners[index] >= 0)
                {
                    var aIndex = row % 2 == 0 ? index : partners[index];
                    var bIndex = row % 2 == 0 ? partners[index] : index;
                    IdMapTexel.StampGradient(ref pixel, row,
                        DecalQuantizer.GradientG(source, layer.PaletteColors[aIndex], layer.PaletteColors[bIndex]));
                }
                else
                {
                    IdMapTexel.StampRow(ref pixel, row, source.A, layer.WriteBlendFromAlpha);
                }

                target[tx, ty] = pixel;
            }
        }
    }
}
