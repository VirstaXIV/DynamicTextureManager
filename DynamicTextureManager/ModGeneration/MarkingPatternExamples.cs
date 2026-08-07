using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Ready-made marking patterns, generated on demand and imported as regular library entries:
/// starting points that double as a guide for what a custom image should look like —
/// grayscale, bright markings on black, tiling in both directions. Regular entries on
/// purpose, so users can rename, delete and study them like their own imports.
/// </summary>
public static class MarkingPatternExamples
{
    public const int Size = 256;

    public static readonly string[] Names = ["Rosettes", "Paw Prints", "Stars"];

    public static Image<Rgba32> Render(string name)
    {
        var image = new Image<Rgba32>(Size, Size);
        for (var y = 0; y < Size; ++y)
        {
            for (var x = 0; x < Size; ++x)
            {
                var v = name switch
                {
                    "Paw Prints" => PawPrints(x + 0.5f, y + 0.5f),
                    "Stars"      => Stars(x + 0.5f, y + 0.5f),
                    _            => Rosettes(x + 0.5f, y + 0.5f),
                };
                var b = (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
                image[x, y] = new Rgba32(b, b, b, 255);
            }
        }

        return image;
    }

    /// <summary>
    /// Leopard rosettes: broken rings on a jittered grid — the classic coat the procedural
    /// Spots style cannot produce. Jitter is keyed on the WRAPPED cell id while positions
    /// use the unwrapped index, so instances repeat exactly across the tile edge.
    /// </summary>
    private static float Rosettes(float x, float y)
    {
        const int   cells = 4;
        const float cell  = Size / (float)cells;

        var cellX = (int)MathF.Floor(x / cell);
        var cellY = (int)MathF.Floor(y / cell);
        var v     = 0f;
        for (var oy = -1; oy <= 1; ++oy)
        {
            for (var ox = -1; ox <= 1; ++ox)
            {
                var gx = Mod(cellX + ox, cells);
                var gy = Mod(cellY + oy, cells);
                var cx = (cellX + ox + 0.5f) * cell + (Hash(gx, gy, 1) - 0.5f) * cell * 0.4f;
                var cy = (cellY + oy + 0.5f) * cell + (Hash(gx, gy, 2) - 0.5f) * cell * 0.4f;

                var dx = x - cx;
                var dy = y - cy;
                var d  = MathF.Sqrt(dx * dx + dy * dy);
                var r  = cell * (0.26f + 0.1f * Hash(gx, gy, 3));

                var ring = 1f - ProceduralFields.Smooth(2.5f, 5.5f, MathF.Abs(d - r));

                // Three arcs with gaps between them, rotated per rosette.
                var arcs = MathF.Cos(3f * MathF.Atan2(dy, dx) + Hash(gx, gy, 4) * MathF.Tau);
                ring *= ProceduralFields.Smooth(-0.45f, -0.05f, arcs);

                v = MathF.Max(v, ring);
            }
        }

        return v;
    }

    /// <summary> A diagonal walking track: two paw prints per tile, each a pad plus four toes. </summary>
    private static float PawPrints(float x, float y)
    {
        var v = Paw(x, y, 64f, 72f, -0.35f);
        return MathF.Max(v, Paw(x, y, 192f, 200f, 0.3f));
    }

    private static float Paw(float x, float y, float centerX, float centerY, float rotation)
    {
        var dx  = WrapDelta(x, centerX);
        var dy  = WrapDelta(y, centerY);
        var cos = MathF.Cos(rotation);
        var sin = MathF.Sin(rotation);
        var px  = dx * cos + dy * sin;
        var py  = -dx * sin + dy * cos;

        // Pad below, four separated toes hugging its upper edge on an arc — outer toes
        // sit lower than the middle pair, like a real print.
        var v = Ellipse(px, py - 6f, 15f, 12f);
        for (var toe = 0; toe < 4; ++toe)
        {
            var angle = -0.8f + 1.6f / 3f * toe;
            var toeX  = MathF.Sin(angle) * 22f;
            var toeY  = 6f - MathF.Cos(angle) * 19f;
            v = MathF.Max(v, Ellipse(px - toeX, py - toeY, 4.8f, 6.2f));
        }

        return v;
    }

    /// <summary> Five-pointed stars of varying size and rotation on a jittered grid. </summary>
    private static float Stars(float x, float y)
    {
        const int   cells = 4;
        const float cell  = Size / (float)cells;

        var cellX = (int)MathF.Floor(x / cell);
        var cellY = (int)MathF.Floor(y / cell);
        var v     = 0f;
        for (var oy = -1; oy <= 1; ++oy)
        {
            for (var ox = -1; ox <= 1; ++ox)
            {
                var gx = Mod(cellX + ox, cells);
                var gy = Mod(cellY + oy, cells);
                var cx = (cellX + ox + 0.5f) * cell + (Hash(gx, gy, 5) - 0.5f) * cell * 0.5f;
                var cy = (cellY + oy + 0.5f) * cell + (Hash(gx, gy, 6) - 0.5f) * cell * 0.5f;

                var dx = x - cx;
                var dy = y - cy;
                var d  = MathF.Sqrt(dx * dx + dy * dy);
                var r  = cell * (0.2f + 0.16f * Hash(gx, gy, 7));

                // Five rounded spikes: the boundary radius swells toward each spike direction.
                var angle  = MathF.Atan2(dy, dx) + Hash(gx, gy, 8) * MathF.Tau;
                var spike  = MathF.Pow(0.5f + 0.5f * MathF.Cos(5f * angle), 6f);
                var radius = r * (0.35f + 0.65f * spike);

                v = MathF.Max(v, 1f - ProceduralFields.Smooth(-1.5f, 1.5f, d - radius));
            }
        }

        return v;
    }

    private static float Ellipse(float x, float y, float a, float b)
    {
        var q = x / a * (x / a) + y / b * (y / b);
        return 1f - ProceduralFields.Smooth(0.8f, 1f, q);
    }

    /// <summary> Shortest wrapped distance between two coordinates on the tile. </summary>
    private static float WrapDelta(float a, float b)
    {
        var d = a - b;
        return d - MathF.Round(d / Size) * Size;
    }

    private static int Mod(int a, int m)
        => (a % m + m) % m;

    private static float Hash(int x, int y, int salt)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + salt * 2246822519);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
        }
    }
}
