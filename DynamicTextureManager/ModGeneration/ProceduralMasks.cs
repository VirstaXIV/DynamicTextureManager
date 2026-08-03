using System;
using System.Numerics;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Deterministic procedural masks for texture-space adjustments (hair highlight modulation):
/// seeded value noise with fBm octaves and directional UV gradients. Pure integer/float math
/// with no library RNG, so the same parameters always produce the same bytes — two builds of
/// the same dTexture stay byte-identical, and the preview cache matches the built files.
/// Sampling happens in normalized UV space, so results are resolution-independent too.
/// </summary>
public static class ProceduralMasks
{
    /// <summary> Seeded integer-avalanche hash of a lattice point, uniform in [0,1]. </summary>
    private static float Hash01(int seed, int x, int y)
    {
        var h = (uint)seed;
        h ^= (uint)x * 0x85EBCA6Bu;
        h ^= (uint)y * 0xC2B2AE35u;
        h *= 0x27D4EB2Fu;
        h ^= h >> 15;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        return (h & 0xFFFFFF) / 16777215f;
    }

    private static float SmoothStep(float t)
        => t * t * (3f - 2f * t);

    /// <summary> Lattice value noise in [0,1], smoothstep-interpolated between hashed corners. </summary>
    public static float ValueNoise(int seed, Vector2 p)
    {
        var x0 = (int)MathF.Floor(p.X);
        var y0 = (int)MathF.Floor(p.Y);
        var tx = SmoothStep(p.X - x0);
        var ty = SmoothStep(p.Y - y0);

        var a = Hash01(seed, x0, y0);
        var b = Hash01(seed, x0 + 1, y0);
        var c = Hash01(seed, x0, y0 + 1);
        var d = Hash01(seed, x0 + 1, y0 + 1);

        var top    = a + (b - a) * tx;
        var bottom = c + (d - c) * tx;
        return top + (bottom - top) * ty;
    }

    /// <summary>
    /// Fractal value noise: octaves at doubling frequency and halving amplitude, each with its
    /// own derived seed, normalized back to [0,1].
    /// </summary>
    public static float Fbm(int seed, Vector2 p, int octaves)
    {
        octaves = Math.Clamp(octaves, 1, 8);
        var sum       = 0f;
        var amplitude = 1f;
        var total     = 0f;
        for (var i = 0; i < octaves; ++i)
        {
            sum       += ValueNoise(seed + i * 1013, p) * amplitude;
            total     += amplitude;
            amplitude *= 0.5f;
            p         *= 2f;
        }

        return sum / total;
    }
}
