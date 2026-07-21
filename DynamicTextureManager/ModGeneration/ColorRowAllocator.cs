using System.Collections.Generic;
using System.Linq;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Assigns free colorset slots to a multi-color decal. The ID map's G channel BLENDS a
/// pair's A row toward its B row and the game samples it with interpolation, so edge texels
/// always mix the two halves of a pair — a pair can only ever hold ONE color: its A row
/// carries it and its B row a darkened shade, making the blend a benign darkening. Each
/// color therefore claims one whole free pair.
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
    /// Pick one whole free pair per color, returning the A rows. Pairs the gear renders are
    /// blocked entirely — their texels carry intermediate G blends between both halves, so
    /// claiming either half would recolor the garment. Pairs any half of which another decal
    /// claims are blocked too.
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

        if (freePairs.Count < colorCount)
            return new AllocationResult([],
                $"Decal needs {colorCount} free colorset slot(s) but only {freePairs.Count} are fully free on this material — lower Max Colors or remove other decals.");

        return new AllocationResult(freePairs.Take(colorCount).Select(pair => (pair - 1) * 2).ToList(), null);
    }
}
