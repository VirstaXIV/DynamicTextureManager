using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Services;
using Penumbra.GameData.Files;
using Penumbra.GameData.Files.MaterialStructs;
using SixLabors.ImageSharp;
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// The colorset-decal extraction round trip: lift a decal already baked into a texture's id
/// map out into a movable stamp layer (<see cref="ColorsetDecalExtractor"/> finds it,
/// <see cref="ColorRowAllocator"/> relocates its rows onto free pairs), and maintain the
/// texture's CLEANED SOURCE copy — its true base with every extracted footprint erased — so
/// builds and previews start from a map that no longer contains the extracted decals.
/// UI-free: selection state and status display stay in the decals tab.
/// </summary>
public sealed class ColorsetExtractionWorkflow(OverlayModManager overlayMods, TextureIO textureIO,
    DecalLibrary decals, FilenameService filenames, CompositePreviewCache previewCache, ColorsetRowLedger ledger)
{
    /// <summary> Copy an extracted layer's temp stamp into the library — the explicit opt-in step. Returns false on failure. </summary>
    public bool AddExtractedToLibrary(DecalLayer decal, string label)
    {
        try
        {
            using var image = Image.Load<Rgba32>(decals.LayerImagePath(decal));
            var entry = decals.ImportGenerated(image, label);
            if (entry == null)
                return false;

            decal.LibraryCopyId = entry.Id;
            return true;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Could not add the extracted decal to the library:\n{ex}");
            return false;
        }
    }

    /// <summary>
    /// Extract the selected rows of a texture's id map into a new decal layer. On success the
    /// layer is added, the cleaned source is rebuilt and the claimed slots are seeded; the
    /// caller persists the dTexture. The status string reports the result either way.
    /// </summary>
    /// <param name="materialOf"> Resolves a texture game path to its material game path (null when unknown). </param>
    public (bool Success, string Status) Extract(DTexture dTexture, string textureGamePath, string materialGamePath,
        MtrlFile mtrl, ColorTable table, IReadOnlyCollection<int> rows, bool largestOnly, Func<string, string?> materialOf)
    {
        // An empty capture means vanilla — TextureIO.Load falls back to game data for it.
        var diskPath   = overlayMods.GetOrCaptureTextureSource(dTexture, textureGamePath);
        var decoded    = textureIO.Load(textureGamePath, diskPath, null);
        var rowDiffuse = MaterialEditApplier.ResolveRowDiffuse(mtrl, null);
        if (decoded == null || rowDiffuse == null)
            return (false, "Could not load the id map or its colorset.");

        var extraction = ColorsetDecalExtractor.Extract(decoded, rows, rowDiffuse, largestOnly);
        if (extraction == null)
            return (false, "The selected rows cover no texels — nothing to extract.");

        // The extracted content moves onto freshly claimed slots: its source rows may be
        // shared with the garment (decal on 3B, cloth on 3A), so keeping them would couple
        // every recolor to the gear. One whole free pair per source row, like any decal.
        ledger.EnsureIdStats(dTexture, textureGamePath);
        var others     = ledger.ClaimedRowsForMaterial(dTexture, materialGamePath, null, materialOf);
        var allocation = ColorRowAllocator.Allocate(extraction.Rows.Count,
            ledger.EffectiveGearUsedPairs(dTexture, materialGamePath), others);
        if (!allocation.Success)
            return (false, allocation.Error!);

        // The stamp is a temp file owned by this dTexture, NOT a library entry — re-running
        // the extraction must never pile up duplicates in the library. "Add to Library" on
        // the layer is the explicit step that keeps it for reuse.
        var stampFile = $"{dTexture.Identifier:N}_stamp_{Guid.NewGuid():N}.png";
        try
        {
            Directory.CreateDirectory(filenames.ExtractedDirectory);
            using var stamp = extraction.Stamp;
            stamp.SaveAsPng(Path.Combine(filenames.ExtractedDirectory, stampFile));
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Could not save the extracted stamp image:\n{ex}");
            return (false, "Could not save the extracted stamp image.");
        }

        overlayMods.GetOrCaptureTextureSource(dTexture, textureGamePath);
        if (!dTexture.Data.Textures.TryGetValue(textureGamePath, out var layers))
        {
            layers                                  = [];
            dTexture.Data.Textures[textureGamePath] = layers;
        }

        var layer = new DecalLayer
        {
            LocalImageFile      = stampFile,
            IdRemap             = true,
            Extracted           = true,
            WriteBlendFromAlpha = true,
            PaletteColors       = extraction.RowColors.ToList(),
            PaletteRows         = allocation.Rows,
            MaxColors           = extraction.Rows.Count,
            FillPair            = extraction.FillPair,
            FillBlend           = extraction.FillBlend,
            SourceU             = (float)extraction.X / extraction.MapWidth,
            SourceV             = (float)extraction.Y / extraction.MapHeight,
            SourceUW            = (float)extraction.W / extraction.MapWidth,
            SourceUH            = (float)extraction.H / extraction.MapHeight,
            Surface             = false,
        };
        // Texel-exact original placement, so the restamp lands exactly on the erased region.
        layer.PosU   = layer.SourceU + layer.SourceUW / 2f;
        layer.PosV   = layer.SourceV + layer.SourceUH / 2f;
        layer.ScaleX = layer.SourceUW;
        layer.ScaleY = layer.SourceUH;

        // The texture's source becomes a cleaned copy with the decal removed; the original
        // is remembered so removing the extraction returns the source to the base mod. A
        // second extraction on the same texture shares the first one's true base.
        layer.PreExtractionSource = layers.OfType<DecalLayer>()
                .FirstOrDefault(l => l is { Extracted: true, PreExtractionSource: not null })?.PreExtractionSource
         ?? dTexture.Data.TextureSourcePaths.GetValueOrDefault(textureGamePath)
         ?? string.Empty;
        layers.Add(layer);
        RegenerateCleanedSource(dTexture, textureGamePath);

        // Seed each claimed slot from its SOURCE row so the decal keeps its authored look
        // (specular, roughness, tile — everything, not just the color); the slot's B half
        // becomes the standard darkened shade partner for benign edge blends.
        var edit = ledger.GetOrAddMaterialEdit(dTexture, materialGamePath, mtrl.ShaderPackage.Name);
        for (var i = 0; i < allocation.Rows.Count; ++i)
        {
            var newRow = allocation.Rows[i];
            var srcRow = extraction.Rows[i];
            edit.Rows.Remove(newRow);
            edit.Rows.Remove(newRow + 1);

            var seededA = ledger.GetOrSeedRow(edit, table, newRow, srcRow);
            var seededB = ledger.GetOrSeedRow(edit, table, newRow + 1, srcRow);
            seededB.Diffuse =
            [
                seededA.Diffuse[0] * ColorsetColorDomain.ShadeFactor,
                seededA.Diffuse[1] * ColorsetColorDomain.ShadeFactor,
                seededA.Diffuse[2] * ColorsetColorDomain.ShadeFactor,
            ];
        }

        DynamicTextureManager.Log.Information(
            $"Extracted colorset decal from {textureGamePath}: rows [{string.Join(", ", extraction.Rows.Select(RowName))}] -> "
          + $"slots [{string.Join(", ", allocation.Rows.Select(r => r / 2 + 1))}], "
          + $"rect {extraction.X},{extraction.Y} {extraction.W}x{extraction.H}, fill pair {extraction.FillPair + 1} blend {extraction.FillBlend}.");
        return (true,
            $"Extracted {extraction.Rows.Count} row(s) into a decal layer ({extraction.W}x{extraction.H} texels), "
          + $"relocated onto slot(s) {string.Join(", ", allocation.Rows.Select(r => r / 2 + 1))}. "
          + "The texture's source is now a cleaned copy with the decal removed — anything left behind shows in the row list above.");
    }

    /// <summary>
    /// Rebuild the cleaned source copy of a texture: its true base (the source before any
    /// extraction) with every extracted decal's footprint erased, written next to the config
    /// and set as the texture's captured source. Builds and previews then start from a map
    /// that no longer contains the extracted decals.
    /// </summary>
    public void RegenerateCleanedSource(DTexture dTexture, string gamePath)
    {
        var extracted = dTexture.Data.Textures.GetValueOrDefault(gamePath)?.OfType<DecalLayer>()
                .Where(l => l is { Extracted: true, PreExtractionSource: not null }).ToList()
         ?? [];
        if (extracted.Count == 0)
            return;

        var basePath = extracted[0].PreExtractionSource!;
        var decoded  = textureIO.Load(gamePath, basePath, null);
        if (decoded == null)
        {
            DynamicTextureManager.Log.Warning($"Could not load the base source of {gamePath} to build its cleaned copy.");
            return;
        }

        using var image = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(decoded.Rgba, decoded.Width, decoded.Height);
        foreach (var layer in extracted)
            TextureCompositor.EraseExtractedFootprint(image, layer, decals.LayerImagePath(layer));

        var file = filenames.ExtractedSourceFile(dTexture.Identifier, gamePath);
        Directory.CreateDirectory(filenames.ExtractedDirectory);
        image.SaveAsPng(file);
        dTexture.Data.TextureSourcePaths[gamePath] = file;
        ledger.Invalidate();
        previewCache.Invalidate(dTexture.Identifier, gamePath);
        DynamicTextureManager.Log.Information(
            $"Rebuilt cleaned source of {gamePath} from \"{(basePath.Length == 0 ? "vanilla" : basePath)}\" minus {extracted.Count} extracted decal(s).");
    }

    /// <summary>
    /// After removing an extracted layer: regenerate the cleaned copy from the remaining
    /// extractions, or — when it was the last one — restore the original source capture and
    /// delete the copy, returning the texture to the base mod.
    /// </summary>
    public void RestoreOrRegenerateSource(DTexture dTexture, string gamePath, DecalLayer removed)
    {
        var remaining = dTexture.Data.Textures.GetValueOrDefault(gamePath)?.OfType<DecalLayer>()
            .Any(l => l is { Extracted: true, PreExtractionSource: not null }) ?? false;
        if (remaining)
        {
            RegenerateCleanedSource(dTexture, gamePath);
            return;
        }

        dTexture.Data.TextureSourcePaths[gamePath] = removed.PreExtractionSource!;
        try
        {
            File.Delete(filenames.ExtractedSourceFile(dTexture.Identifier, gamePath));
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not delete the cleaned source copy of {gamePath}: {ex.Message}");
        }

        ledger.Invalidate();
        previewCache.Invalidate(dTexture.Identifier, gamePath);
        DynamicTextureManager.Log.Information(
            $"Removed last extraction of {gamePath} — source restored to \"{(removed.PreExtractionSource!.Length == 0 ? "vanilla" : removed.PreExtractionSource)}\".");
    }

    private static string RowName(int row)
        => $"{row / 2 + 1}{(row % 2 == 0 ? 'A' : 'B')}";
}
