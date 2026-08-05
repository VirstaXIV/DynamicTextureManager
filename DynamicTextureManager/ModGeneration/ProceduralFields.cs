using System;
using System.Numerics;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Deterministic noise primitives for the procedural surface generators, extending
/// <see cref="ProceduralMasks"/> into 3D: seeded value noise, fBm, domain warping and
/// cellular (Worley) noise. Same hard rule — pure integer/float math with no library RNG,
/// so the same parameters always produce the same bytes and previews match built files.
/// The 3D world-space variants are the backbone: evaluated at mesh surface positions they
/// need no UV parametrization and cannot show seams between UV islands.
/// </summary>
public static class ProceduralFields
{
    /// <summary> Seeded integer-avalanche hash of a 3D lattice point, uniform in [0,1]. </summary>
    public static float Hash01(int seed, int x, int y, int z)
        => (Hash(seed, x, y, z) & 0xFFFFFF) / 16777215f;

    private static uint Hash(int seed, int x, int y, int z)
    {
        var h = (uint)seed;
        h ^= (uint)x * 0x85EBCA6Bu;
        h ^= (uint)y * 0xC2B2AE35u;
        h ^= (uint)z * 0x9E3779B1u;
        h *= 0x27D4EB2Fu;
        h ^= h >> 15;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        return h;
    }

    private static float SmoothStep(float t)
        => t * t * (3f - 2f * t);

    /// <summary> 3D lattice value noise in [0,1], smoothstep-interpolated between hashed corners. </summary>
    public static float ValueNoise3(int seed, Vector3 p)
    {
        var x0 = (int)MathF.Floor(p.X);
        var y0 = (int)MathF.Floor(p.Y);
        var z0 = (int)MathF.Floor(p.Z);
        var tx = SmoothStep(p.X - x0);
        var ty = SmoothStep(p.Y - y0);
        var tz = SmoothStep(p.Z - z0);

        var c000 = Hash01(seed, x0, y0, z0);
        var c100 = Hash01(seed, x0 + 1, y0, z0);
        var c010 = Hash01(seed, x0, y0 + 1, z0);
        var c110 = Hash01(seed, x0 + 1, y0 + 1, z0);
        var c001 = Hash01(seed, x0, y0, z0 + 1);
        var c101 = Hash01(seed, x0 + 1, y0, z0 + 1);
        var c011 = Hash01(seed, x0, y0 + 1, z0 + 1);
        var c111 = Hash01(seed, x0 + 1, y0 + 1, z0 + 1);

        var x00 = c000 + (c100 - c000) * tx;
        var x10 = c010 + (c110 - c010) * tx;
        var x01 = c001 + (c101 - c001) * tx;
        var x11 = c011 + (c111 - c011) * tx;

        var y0v = x00 + (x10 - x00) * ty;
        var y1v = x01 + (x11 - x01) * ty;
        return y0v + (y1v - y0v) * tz;
    }

    /// <summary>
    /// 3D fractal value noise: octaves at doubling frequency and halving amplitude, each with
    /// its own derived seed, normalized back to [0,1].
    /// </summary>
    public static float Fbm3(int seed, Vector3 p, int octaves)
    {
        octaves = Math.Clamp(octaves, 1, 8);
        var sum       = 0f;
        var amplitude = 1f;
        var total     = 0f;
        for (var i = 0; i < octaves; ++i)
        {
            sum       += ValueNoise3(seed + i * 1013, p) * amplitude;
            total     += amplitude;
            amplitude *= 0.5f;
            p         *= 2f;
        }

        return sum / total;
    }

    /// <summary>
    /// Displace a sample position by three decorrelated fBm fields — the classic domain warp
    /// that turns blobby value noise into flowing organic shapes (marbling, dapple).
    /// </summary>
    public static Vector3 DomainWarp3(int seed, Vector3 p, float strength)
    {
        if (strength == 0f)
            return p;

        var warp = new Vector3(
            Fbm3(seed + 101, p, 3) - 0.5f,
            Fbm3(seed + 202, p, 3) - 0.5f,
            Fbm3(seed + 303, p, 3) - 0.5f);
        return p + warp * (2f * strength);
    }

    /// <summary> One cellular-noise evaluation, see <see cref="Worley"/>. </summary>
    public readonly record struct WorleySample(float F1, float F2, uint CellHash)
    {
        /// <summary> Distance from the nearest cell edge, roughly — 0 at borders, large at centers. </summary>
        public float EdgeDist
            => F2 - F1;
    }

    /// <summary>
    /// 2D cellular (Worley) noise: one feature point per lattice cell, placed by the hash.
    /// Returns the nearest and second-nearest feature distances (in cell units) plus the
    /// nearest cell's hash for stable per-cell variation.
    /// </summary>
    public static WorleySample Worley(int seed, Vector2 p)
    {
        var cx = (int)MathF.Floor(p.X);
        var cy = (int)MathF.Floor(p.Y);

        var f1   = float.MaxValue;
        var f2   = float.MaxValue;
        var hash = 0u;

        for (var dy = -1; dy <= 1; ++dy)
        {
            for (var dx = -1; dx <= 1; ++dx)
            {
                var gx = cx + dx;
                var gy = cy + dy;
                var h  = Hash(seed, gx, gy, 0);
                var fx = gx + (h & 0xFFF) / 4095f;
                var fy = gy + ((h >> 12) & 0xFFF) / 4095f;

                var d = (p - new Vector2(fx, fy)).LengthSquared();
                if (d < f1)
                {
                    f2   = f1;
                    f1   = d;
                    hash = h;
                }
                else if (d < f2)
                {
                    f2 = d;
                }
            }
        }

        return new WorleySample(MathF.Sqrt(f1), MathF.Sqrt(f2), hash);
    }

    /// <summary> Hermite threshold: 0 below <paramref name="a"/>, 1 above <paramref name="b"/>. </summary>
    public static float Smooth(float a, float b, float t)
    {
        if (a >= b)
            return t < a ? 0f : 1f;

        var x = Math.Clamp((t - a) / (b - a), 0f, 1f);
        return x * x * (3f - 2f * x);
    }
}
