using System;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Pixel-buffer filters for decal preparation. Correct linear downscaling fades sub-texel
/// detail: a line thinner than a texel area-averages into a faint smear, where a texture
/// artist would redraw it as a thin FULL-CONTRAST line — which is why hand-authored tattoos
/// read sharper than a filtered shrink at the same resolution. The unsharp mask here puts
/// that contrast back after a heavy downscale.
/// </summary>
public static class ImageFilters
{
    /// <summary>
    /// Unsharp mask over premultiplied RGBA: blur with a 3x3 Gaussian, then push each pixel
    /// away from its blurred neighborhood by <paramref name="amount"/>. Premultiplying first
    /// keeps fully transparent pixels from dragging edge colors toward their (meaningless)
    /// RGB, so soft edges sharpen without dark halos; alpha sharpens along, restoring the
    /// coverage contrast of fine lines.
    /// </summary>
    public static void SharpenPremultiplied(Rgba32[] pixels, int width, int height, float amount)
    {
        if (amount <= 0f || width < 3 || height < 3)
            return;

        // Premultiplied float planes.
        var r = new float[pixels.Length];
        var g = new float[pixels.Length];
        var b = new float[pixels.Length];
        var a = new float[pixels.Length];
        for (var i = 0; i < pixels.Length; ++i)
        {
            var p  = pixels[i];
            var pa = p.A / 255f;
            a[i] = pa;
            r[i] = p.R / 255f * pa;
            g[i] = p.G / 255f * pa;
            b[i] = p.B / 255f * pa;
        }

        var blurR = Blur3(r, width, height);
        var blurG = Blur3(g, width, height);
        var blurB = Blur3(b, width, height);
        var blurA = Blur3(a, width, height);

        for (var i = 0; i < pixels.Length; ++i)
        {
            var sa = Math.Clamp(a[i] + amount * (a[i] - blurA[i]), 0f, 1f);
            var sr = Math.Clamp(r[i] + amount * (r[i] - blurR[i]), 0f, 1f);
            var sg = Math.Clamp(g[i] + amount * (g[i] - blurG[i]), 0f, 1f);
            var sb = Math.Clamp(b[i] + amount * (b[i] - blurB[i]), 0f, 1f);

            if (sa <= 0f)
            {
                pixels[i] = new Rgba32(0, 0, 0, 0);
                continue;
            }

            pixels[i] = new Rgba32(
                (byte)Math.Clamp((int)MathF.Round(sr / sa * 255f), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(sg / sa * 255f), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(sb / sa * 255f), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(sa * 255f), 0, 255));
        }
    }

    /// <summary> Separable 3x3 Gaussian ([1 2 1]/4 per axis), edge-clamped. </summary>
    private static float[] Blur3(float[] source, int width, int height)
    {
        var tmp = new float[source.Length];
        var ret = new float[source.Length];

        for (var y = 0; y < height; ++y)
        {
            var row = y * width;
            for (var x = 0; x < width; ++x)
            {
                var l = source[row + Math.Max(0, x - 1)];
                var c = source[row + x];
                var rr = source[row + Math.Min(width - 1, x + 1)];
                tmp[row + x] = (l + 2f * c + rr) * 0.25f;
            }
        }

        for (var y = 0; y < height; ++y)
        {
            var up   = Math.Max(0, y - 1) * width;
            var row  = y * width;
            var down = Math.Min(height - 1, y + 1) * width;
            for (var x = 0; x < width; ++x)
                ret[row + x] = (tmp[up + x] + 2f * tmp[row + x] + tmp[down + x]) * 0.25f;
        }

        return ret;
    }
}
