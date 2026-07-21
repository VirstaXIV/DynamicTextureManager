using System.Collections.Generic;
using System.Linq;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Assigns free colorset rows to a multi-color decal. The ID map's G channel BLENDS a pair's
/// A row toward its B row and the game samples it with interpolation, so edge texels always
/// mix the two halves of a pair — a pair must therefore never hold colors of two different
/// decals, or edges fringe with a foreign color. Each decal claims whole pairs: two of its
/// own (luminance-adjacent, hence similar) colors per pair, so edge blending stays subtle.
/// </summary>
public static class ColorRowAllocator
{
    public const int RowCount  = 32;
    public const int PairCount = 16;

    public sealed record AllocationResult(List<int> Rows, string? Error)
    {
        public bool Success
            => Error == null;
    }

    /// <summary>
    /// Pick whole free pairs for <paramref name="colorCount"/> colors (two per pair, A first).
    /// Pairs the gear renders are blocked entirely — their texels carry intermediate G blends
    /// between both halves, so claiming either half would recolor the garment. Pairs any half
    /// of which another decal claims are blocked too.
    /// </summary>
    public static AllocationResult Allocate(int colorCount, IReadOnlySet<int> gearUsedPairs, IReadOnlySet<int> claimedRows)
    {
        var freePairs = new List<int>();
        for (var pair = 1; pair <= PairCount; ++pair)
        {
            var rowA = (pair - 1) * 2;
            if (gearUsedPairs.Contains(pair) || claimedRows.Contains(rowA) || claimedRows.Contains(rowA + 1))
                continue;

            freePairs.Add(pair);
        }

        var needed = (colorCount + 1) / 2;
        if (freePairs.Count < needed)
            return new AllocationResult([],
                $"Decal needs {colorCount} color(s) ({needed} free colorset pair(s)) but only {freePairs.Count} pair(s) are fully free on this material — lower Max Colors or remove other decals.");

        var rows = freePairs.Take(needed)
            .SelectMany(pair => new[] { (pair - 1) * 2, (pair - 1) * 2 + 1 })
            .Take(colorCount)
            .ToList();
        return new AllocationResult(rows, null);
    }
}
