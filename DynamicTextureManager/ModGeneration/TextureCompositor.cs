using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using DynamicTextureManager.DTextures.Data;
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

    private void ApplyDecal(Image<Rgba32> target, DecalLayer layer, MaterialMesh? mesh, bool previewHighlight)
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
            SurfaceDecalBaker.Bake(target, source, mesh, layer, previewHighlight);
            return;
        }

        var width  = Math.Max(1, (int)Math.Round(layer.ScaleX * target.Width));
        var height = Math.Max(1, (int)Math.Round(layer.ScaleY * target.Height));

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

        if (layer.IdRemap)
            ApplyIdRemap(target, decal, layer, x, y);
        else
            target.Mutate(c => c.DrawImage(decal, new Point(x, y), layer.Opacity));
    }

    /// <summary>
    /// Colorset decal: each opaque decal pixel is nearest-mapped to the layer's extracted
    /// palette and its ID texel remapped to the claimed row rendering that color — R selects
    /// the row pair, G selects the half (255 = A, 0 = B). Writing G flattens the garment's
    /// baked shading inside the decal, which is the price of using both halves as
    /// independent colors.
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
                pixel.G        = row % 2 == 0 ? (byte)255 : (byte)0;
                target[tx, ty] = pixel;
            }
        }
    }
}
