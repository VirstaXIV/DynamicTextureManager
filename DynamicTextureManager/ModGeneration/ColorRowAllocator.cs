using System.Collections.Generic;
using System.Linq;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Assigns free colorset half-rows to a multi-color decal. A and B halves of a pair count as
/// two independent colors (the bake writes the ID map's G channel explicitly), so a
/// Dawntrail table offers up to 32 slots minus whatever the gear and other decals use.
/// </summary>
public static class ColorRowAllocator
{
    public const int RowCount = 32;

    public sealed record AllocationResult(List<int> Rows, string? Error)
    {
        public bool Success
            => Error == null;
    }

    /// <summary>
    /// Pick <paramref name="colorCount"/> free half-rows. Pairs the gear renders are blocked
    /// entirely — their texels carry intermediate G blends between both halves, so claiming
    /// either half would recolor the garment. Rows claimed by other decals are skipped too.
    /// </summary>
    public static AllocationResult Allocate(int colorCount, IReadOnlySet<int> gearUsedPairs, IReadOnlySet<int> claimedRows)
    {
        var free = new List<int>();
        for (var row = 0; row < RowCount; ++row)
        {
            var pair = row / 2 + 1;
            if (gearUsedPairs.Contains(pair) || claimedRows.Contains(row))
                continue;

            free.Add(row);
        }

        if (free.Count < colorCount)
            return new AllocationResult([],
                $"Decal needs {colorCount} free colorset rows but only {free.Count} are available on this material — lower Max Colors or remove other decals.");

        return new AllocationResult(free.Take(colorCount).ToList(), null);
    }
}
