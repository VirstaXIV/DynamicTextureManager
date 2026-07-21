using System.Numerics;
using DynamicTextureManager.DTextures.Data;
using Penumbra.GameData.Files;
using Penumbra.GameData.Files.MaterialStructs;

namespace DynamicTextureManager.ModGeneration;

/// <summary> Applies a dTexture's colorset edits onto a parsed material, shared by the mod build and the live preview. </summary>
public static class MaterialEditApplier
{
    /// <summary> Apply all rows of an edit to the material in place. Returns the number of rows applied. </summary>
    public static int Apply(MtrlFile mtrl, MaterialEdit edit)
    {
        if (mtrl.Table is not ColorTable table)
            return 0;

        var applied = 0;
        foreach (var row in edit.Rows.Values)
        {
            if (row.RowIndex < 0 || row.RowIndex >= ColorTable.NumRows)
            {
                DynamicTextureManager.Log.Warning($"Colorset row {row.RowIndex} out of range, skipped.");
                continue;
            }

            table[row.RowIndex] = row.ToRow();

            if (mtrl.DyeTable is ColorDyeTable dyeTable)
                switch (row.DyeMode)
                {
                    // Applied stains override row values for all dye-flagged properties, so an
                    // edited row only shows on dyed gear once its dye entry is cleared.
                    case ColorRowEdit.RowDyeMode.Disable:
                        dyeTable[row.RowIndex] = default;
                        break;
                    // Custom entries make claimed rows (e.g. colorset decals on previously
                    // unused pairs) respond to dyes like the rest of the gear.
                    case ColorRowEdit.RowDyeMode.Custom:
                        dyeTable[row.RowIndex] = new ColorDyeTableRow
                        {
                            Template      = row.DyeTemplate,
                            Channel       = row.DyeChannel,
                            DiffuseColor  = row.DyeDiffuse,
                            SpecularColor = row.DyeSpecular,
                            EmissiveColor = row.DyeEmissive,
                            Roughness     = row.DyeRoughness,
                            Metalness     = row.DyeMetalness,
                            SheenRate     = row.DyeSheen,
                            SheenTintRate = row.DyeSheen,
                            SheenAperture = row.DyeSheen,
                        };
                        break;
                }

            ++applied;
        }

        return applied;
    }

    /// <summary>
    /// The 32 row diffuse colors of a material's colorset with a dTexture's edits applied —
    /// what the colorset visualizations (id-map colorize, 3D preview) render rows with.
    /// Null for materials without a Dawntrail color table.
    /// </summary>
    public static Vector3[]? ResolveRowDiffuse(MtrlFile mtrl, MaterialEdit? edit)
    {
        if (mtrl.Table is not ColorTable)
            return null;

        var resolved = mtrl;
        if (edit is { IsEmpty: false })
        {
            resolved = CloneForEdit(mtrl);
            Apply(resolved, edit);
        }

        if (resolved.Table is not ColorTable table)
            return null;

        var ret = new Vector3[ColorTable.NumRows];
        for (var i = 0; i < ColorTable.NumRows; ++i)
        {
            var color = table[i].DiffuseColor;
            ret[i] = new Vector3((float)color.Red, (float)color.Green, (float)color.Blue);
        }

        return ret;
    }

    /// <summary>
    /// Clone a material so its color and dye tables can be mutated without affecting the
    /// original — <see cref="MtrlFile.Clone"/> copies both tables by reference only.
    /// </summary>
    public static MtrlFile CloneForEdit(MtrlFile mtrl)
    {
        var clone = mtrl.Clone();
        // Only deep-copy Dawntrail tables — legacy tables are never mutated by Apply,
        // and the copy constructors would silently convert them to the new format.
        if (clone.Table is ColorTable table)
            clone.Table = new ColorTable(table);
        if (clone.DyeTable is ColorDyeTable dyeTable)
            clone.DyeTable = new ColorDyeTable(dyeTable);
        return clone;
    }
}
