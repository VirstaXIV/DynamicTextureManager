using System;
using System.Collections.Generic;
using System.Linq;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.DTextures.Data;
using Penumbra.GameData.Files;
using Penumbra.GameData.Files.MaterialStructs;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// The bookkeeping behind colorset-decal row claims: which row pairs a texture's id map
/// actually references (usage statistics computed on demand), which rows the decals of a
/// material have claimed, and how claimed slots are seeded — always from an AUTHORED row,
/// because unused filler rows render black in-game despite their white diffuse. One
/// instance per editing tab; the statistics track one texture at a time.
/// </summary>
public sealed class ColorsetRowLedger(OverlayModManager overlayMods, TextureIO textureIO)
{
    private string                        _statsTexture = string.Empty;
    private readonly HashSet<int>         _usedRowPairs = [];
    private readonly Dictionary<int, int> _rowUsageCounts = [];
    private int                           _statsTotalTexels = 1;

    /// <summary> Row pairs the current statistics texture references (1-based pair numbers). </summary>
    public IReadOnlySet<int> UsedRowPairs
        => _usedRowPairs;

    /// <summary> How many texels actually render each row (the G channel blends A at 255 with B at 0). </summary>
    public IReadOnlyDictionary<int, int> RowUsageCounts
        => _rowUsageCounts;

    public int TotalTexels
        => _statsTotalTexels;

    /// <summary> Drop the cached statistics — the next <see cref="EnsureIdStats"/> recomputes them. </summary>
    public void Invalidate()
        => _statsTexture = string.Empty;

    /// <summary>
    /// Id-map usage statistics for a texture: which row pairs it references, how often each
    /// row actually renders (the G channel blends a pair's A row at 255 with its B row at 0)
    /// and how many texels each pair covers. Row seeding and decal extraction depend on
    /// these, so they are computed on demand.
    /// </summary>
    public void EnsureIdStats(DTexture dTexture, string gamePath)
    {
        if (_statsTexture == gamePath)
            return;

        var diskPath = overlayMods.GetOrCaptureTextureSource(dTexture, gamePath);
        var decoded  = textureIO.Load(gamePath, diskPath, null);
        if (decoded == null)
        {
            // Leave the stats empty but marked current — seeding falls back to the first
            // authored row, and a later successful load recomputes them.
            _statsTexture = gamePath;
            _usedRowPairs.Clear();
            _rowUsageCounts.Clear();
            _statsTotalTexels = 1;
            return;
        }

        ComputeIdStats(gamePath, decoded);
    }

    private void ComputeIdStats(string gamePath, DecodedTexture decoded)
    {
        _statsTexture = gamePath;
        _usedRowPairs.Clear();
        _rowUsageCounts.Clear();
        for (var i = 0; i < decoded.Rgba.Length; i += 4)
        {
            _usedRowPairs.Add(IdMapTexel.Pair(decoded.Rgba[i]) + 1);
            var row = IdMapTexel.Row(decoded.Rgba[i], decoded.Rgba[i + 1]);
            _rowUsageCounts[row] = _rowUsageCounts.GetValueOrDefault(row) + 1;
        }

        _statsTotalTexels = Math.Max(1, decoded.Rgba.Length / 4);
    }

    /// <summary>
    /// The scanner's gear-used slots minus the user's usable overrides — what row allocation
    /// actually blocks. The scanner marks a slot used over a single referencing texel, so
    /// the override exists for maps where stray pixels lock out effectively free slots.
    /// </summary>
    public IReadOnlySet<int> EffectiveGearUsedPairs(DTexture dTexture, string materialGamePath)
    {
        if (!dTexture.Data.Materials.TryGetValue(materialGamePath, out var edit) || edit.UsableSlots.Count == 0)
            return _usedRowPairs;

        var ret = new HashSet<int>(_usedRowPairs);
        ret.ExceptWith(edit.UsableSlots);
        return ret;
    }

    /// <summary>
    /// All colorset rows claimed by colorset decals on any texture of this material. A decal
    /// owns the WHOLE pair of every row it renders — the pair's other half either renders
    /// another of its colors or carries its shade partner, and must never go to another decal
    /// (the id map's G channel blends the two halves at every edge texel).
    /// </summary>
    /// <param name="materialOf"> Resolves a texture game path to its material game path (null when unknown). </param>
    public HashSet<int> ClaimedRowsForMaterial(DTexture dTexture, string materialGamePath, DecalLayer? except,
        Func<string, string?> materialOf)
    {
        var ret = new HashSet<int>();
        foreach (var (gamePath, layers) in dTexture.Data.Textures)
        {
            var material = materialOf(gamePath);
            if (material == null || !string.Equals(material, materialGamePath, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var layer in layers.OfType<DecalLayer>())
                if (layer.IdRemap && !ReferenceEquals(layer, except))
                    foreach (var row in layer.PaletteRows)
                    {
                        ret.Add(row);
                        ret.Add(row ^ 1);
                    }
        }

        return ret;
    }

    /// <summary>
    /// Write the decal's finish into every claimed row (both halves of each pair). Rows are
    /// rebased onto a full template row first (keeping only colors and dye settings), so
    /// switching finishes or returning to Keep is idempotent. With an explicit finish the
    /// template must be a DIELECTRIC authored row: metal rows carry BRDF scalars that turn
    /// the diffuse path off, which rendered a white decal as dark grey once the finish
    /// cleared their Metalness.
    /// </summary>
    public void ApplyFinishToClaimedRows(MaterialEdit edit, ColorTable table, DecalLayer decal)
    {
        foreach (var row in decal.PaletteRows.SelectMany(r => new[] { r, r ^ 1 }).Distinct())
        {
            if (!edit.Rows.TryGetValue(row, out var rowEdit))
                continue;

            // Extracted layers render through the gear's own authored look — leave it alone
            // for Keep, and only stamp the absolute finish values on top otherwise.
            if (decal.Extracted)
            {
                if (decal.Finish != DecalFinishMode.Keep)
                    FinishMapping.ApplyToRow(rowEdit, decal);
                continue;
            }

            var templateIdx = SeedTemplateIndex(table, row);
            if (decal.Finish != DecalFinishMode.Keep && (float)table[templateIdx].Metalness >= 0.5f)
                templateIdx = DielectricTemplateIndex(table) ?? templateIdx;

            var seeded = ColorRowEdit.FromRow(row, table[templateIdx]);
            seeded.RowIndex     = row;
            seeded.Diffuse      = rowEdit.Diffuse;
            seeded.DyeMode      = rowEdit.DyeMode;
            seeded.DyeTemplate  = rowEdit.DyeTemplate;
            seeded.DyeChannel   = rowEdit.DyeChannel;
            seeded.DyeDiffuse   = rowEdit.DyeDiffuse;
            seeded.DyeSpecular  = rowEdit.DyeSpecular;
            seeded.DyeEmissive  = rowEdit.DyeEmissive;
            seeded.DyeRoughness = rowEdit.DyeRoughness;
            seeded.DyeMetalness = rowEdit.DyeMetalness;
            seeded.DyeSheen     = rowEdit.DyeSheen;
            edit.Rows[row]      = seeded;

            if (decal.Finish != DecalFinishMode.Keep)
                FinishMapping.ApplyToRow(seeded, decal);
        }
    }

    /// <summary>
    /// The most-rendered authored non-metal row — the template whose BRDF scalars suit a
    /// dielectric print. Null when the gear authors no dielectric rows at all.
    /// </summary>
    public int? DielectricTemplateIndex(ColorTable table)
    {
        foreach (var (idx, _) in _rowUsageCounts.OrderByDescending(kvp => kvp.Value))
            if (idx >= 0 && idx < ColorTable.NumRows && !IsFillerRow(table[idx]) && (float)table[idx].Metalness < 0.5f)
                return idx;

        for (var i = 0; i < ColorTable.NumRows; ++i)
            if (!IsFillerRow(table[i]) && (float)table[i].Metalness < 0.5f)
                return i;

        return null;
    }

    public MaterialEdit GetOrAddMaterialEdit(DTexture dTexture, string materialGamePath, string shaderName)
    {
        if (dTexture.Data.Materials.TryGetValue(materialGamePath, out var edit))
            return edit;

        edit = new MaterialEdit { ShaderName = shaderName };
        dTexture.Data.Materials[materialGamePath] = edit;
        return edit;
    }

    /// <param name="templateRow">
    /// The source row the seed copies its values from; defaults to the safe authored row
    /// <see cref="SeedTemplateIndex"/> picks. Extraction passes the lifted decal's own
    /// source row so the relocated slot keeps its authored look.
    /// </param>
    public ColorRowEdit GetOrSeedRow(MaterialEdit edit, ColorTable table, int rowIndex, int? templateRow = null)
    {
        if (edit.Rows.TryGetValue(rowIndex, out var row))
            return row;

        var seeded = ColorRowEdit.FromRow(rowIndex, table[templateRow ?? SeedTemplateIndex(table, rowIndex)]);
        seeded.RowIndex = rowIndex;
        // Deterministic default for claimed slots: the decal keeps its color unless the
        // user explicitly makes it dyeable — inheriting the template row's dye entry would
        // silently let an applied stain override the picked color.
        seeded.DyeMode      = ColorRowEdit.RowDyeMode.Disable;
        edit.Rows[rowIndex] = seeded;
        return seeded;
    }

    /// <summary>
    /// The source row a claimed slot copies its non-color values from. Unused filler rows
    /// render BLACK in-game despite their white diffuse, so seeding must always start from
    /// an authored row: the slot's own row when the garment author populated it, a B row's
    /// own A partner, else the authored row the id map actually renders the most.
    /// </summary>
    public int SeedTemplateIndex(ColorTable table, int rowIndex)
    {
        if (!IsFillerRow(table[rowIndex]))
            return rowIndex;

        // A filler B row blends with its pair's A row — that A row is the pair's look.
        if (rowIndex % 2 == 1 && !IsFillerRow(table[rowIndex - 1]))
            return rowIndex - 1;

        foreach (var (idx, _) in _rowUsageCounts.OrderByDescending(kvp => kvp.Value))
            if (idx >= 0 && idx < ColorTable.NumRows && !IsFillerRow(table[idx]))
                return idx;

        for (var i = 0; i < ColorTable.NumRows; ++i)
            if (!IsFillerRow(table[i]))
                return i;

        return rowIndex;
    }

    /// <summary> The signature of an untouched colorset row: white diffuse/specular, legacy gloss 20, default tile transform. </summary>
    public static bool IsFillerRow(in ColorTableRow row)
        => (float)row.DiffuseColor.Red == 1f && (float)row.DiffuseColor.Green == 1f && (float)row.DiffuseColor.Blue == 1f
        && (float)row.SpecularColor.Red == 1f && (float)row.SpecularColor.Green == 1f && (float)row.SpecularColor.Blue == 1f
        && (float)row.Scalar3 == 20f
        && (float)row.Roughness == 0f
        && (float)row.TileTransform.UU == 16f && (float)row.TileTransform.VV == 16f;

    /// <summary> Removing a colorset-decal layer releases its claimed row edits unless another layer still uses them. </summary>
    public void CleanupSlotEdits(DTexture dTexture, string materialGamePath, DecalLayer removed,
        Func<string, string?> materialOf)
    {
        if (!removed.IdRemap || removed.PaletteRows.Count == 0)
            return;

        if (!dTexture.Data.Materials.TryGetValue(materialGamePath, out var edit))
            return;

        var others = ClaimedRowsForMaterial(dTexture, materialGamePath, removed, materialOf);
        foreach (var row in removed.PaletteRows.SelectMany(r => new[] { r, r ^ 1 }).Distinct().Where(r => !others.Contains(r)))
            edit.Rows.Remove(row);
        if (edit.IsEmpty)
            dTexture.Data.Materials.Remove(materialGamePath);
    }

    /// <summary> The dye behavior most of this gear uses: the most frequent dye entry of the source material. </summary>
    public static (ushort Template, byte Channel, ColorDyeTableRow Flags)? DetectGarmentDye(MtrlFile mtrl)
    {
        if (mtrl.DyeTable is not ColorDyeTable dyeTable)
            return null;

        var counts = new Dictionary<ushort, (int Count, int Row)>();
        for (var i = 0; i < ColorDyeTable.NumRows; ++i)
        {
            var template = dyeTable[i].Template;
            if (template == 0)
                continue;

            counts[template] = counts.TryGetValue(template, out var existing) ? (existing.Count + 1, existing.Row) : (1, i);
        }

        if (counts.Count == 0)
            return null;

        var best = counts.OrderByDescending(kvp => kvp.Value.Count).First();
        var row  = dyeTable[best.Value.Row];
        return (best.Key, row.Channel, row);
    }
}
