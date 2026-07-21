using System;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// The colorset id-map texel encoding, in one place: R selects a colorset PAIR (pair × 17
/// over 16 pairs), G blends the pair's A row (255) toward its B row (0) — the garment's
/// baked cloth shading.
///
/// INVARIANT: stamping writes R only; G keeps carrying the garment's baked blend, so a
/// decal's claimed pair (A = color, B = darkened shade) darkens exactly where the cloth
/// shades. Sanctioned exceptions, both flowing through this type:
///  1. Relocated extracted decals write G from the stamp's alpha (<see cref="StampRow"/>
///     with writeBlendFromAlpha) — their content lived on specific A/B halves of shared
///     pairs, so the blend must be steered onto the new pair's A row.
///  2. The extraction erase-fill writes both channels (<see cref="PairByte"/> + a fill
///     blend) to restore the surrounding garment values where a baked decal was lifted out.
/// </summary>
public static class IdMapTexel
{
    public const int PairCount = 16;

    /// <summary> 0-based pair selected by an R byte. </summary>
    public static int Pair(byte r)
        => Math.Clamp((int)Math.Round(r / 17f), 0, PairCount - 1);

    /// <summary> The row (0-31) a texel dominantly renders: the half its G blend leans toward. </summary>
    public static int Row(byte r, byte g)
        => Pair(r) * 2 + (g >= 128 ? 0 : 1);

    /// <summary> R byte encoding a 0-based pair. </summary>
    public static byte PairByte(int pair)
        => (byte)Math.Clamp(pair * 17, 0, 255);

    /// <summary> R byte encoding the pair of a row index (0-31). </summary>
    public static byte RowPairByte(int row)
        => PairByte(row / 2);

    /// <summary> The diffuse color a texel renders: its pair's A and B rows blended by G. </summary>
    public static Vector3 BlendedRowColor(Vector3[] rows, byte r, byte g)
    {
        var pair = Pair(r);
        return Vector3.Lerp(rows[pair * 2 + 1], rows[pair * 2], g / 255f);
    }

    /// <summary> Stamp a decal texel onto an id-map pixel — the single write path for decal stamping. </summary>
    public static void StampRow(ref Rgba32 pixel, int row, byte sampleAlpha, bool writeBlendFromAlpha)
    {
        pixel.R = RowPairByte(row);
        if (writeBlendFromAlpha)
            pixel.G = sampleAlpha;
    }
}
