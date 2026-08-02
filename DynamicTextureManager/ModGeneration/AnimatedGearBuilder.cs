using System;
using System.Linq;
using Lumina.Data.Parsing;
using DynamicTextureManager.DTextures.Data;
using Penumbra.GameData.Files;
using Penumbra.GameData.Files.MaterialStructs;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Converts a modern colorset gear material (character.shpk) to the game's scrolling-effect
/// shader so selected colorset rows glow with a moving pattern. Structure replicated from
/// VANILLA characterscroll gear (chara/equipment/e6257 — dumped 2026-08-02): ordinary gear
/// data (own normal/mask/id textures, colorset, dye table) plus a catchlight texture bound
/// as the scroll pattern, the characterscroll key set and constant block, and — per glowing
/// row — an emissive color with the effect field selecting the scroll parameter set.
/// </summary>
public static class AnimatedGearBuilder
{
    /// <summary> Whether a material can take the conversion: modern gear with a Dawntrail colorset. </summary>
    public static bool CanConvert(MtrlFile mtrl)
        => string.Equals(mtrl.ShaderPackage.Name, "character.shpk", StringComparison.OrdinalIgnoreCase)
         && mtrl.Table is ColorTable;

    /// <summary>
    /// Build the converted material: the source's textures and colorset stay; only the
    /// shader package swaps to the reference structure, the effect pattern texture is
    /// appended, and the selected rows get the glow. The source instance is never mutated.
    /// The caller passes the source WITH colorset row edits already applied, so decal slots
    /// and the glow compose.
    /// </summary>
    public static byte[] BuildMaterial(MtrlFile sourceMtrl, AnimatedGearEdit edit, string effectTexturePath)
    {
        var mtrl = sourceMtrl.Clone();

        // The source's own texture paths, found through its samplers.
        string? PathFor(uint samplerId)
        {
            foreach (var sampler in sourceMtrl.ShaderPackage.Samplers)
                if (sampler.SamplerId == samplerId && sampler.TextureIndex < sourceMtrl.Textures.Length)
                    return sourceMtrl.Textures[sampler.TextureIndex].Path;
            return null;
        }

        var normal = PathFor(0x0C5EC1F1);
        var mask   = PathFor(0x8A4E82B6);
        var id     = PathFor(0x565F8FD8);
        if (normal == null || mask == null || id == null)
            throw new InvalidOperationException("gear material lacks a normal, mask or id texture");

        mtrl.ShaderPackage.Name = "characterscroll.shpk";

        // Mimic the PROVEN reference wholesale where shader-variant selection is concerned:
        // keeping the source's flags (0x0C) and its map2 uv-set declaration produced a
        // material the game silently ignored (the gear rendered unaffected) — the reference
        // authors flags 0x1 and declares map1 only, like the hair conversion authors its own.
        mtrl.ShaderPackage.Flags = 0x00000001;
        mtrl.UvSets              = [new MtrlFile.AttributeSet { Name = "map1", Index = 0 }];
        mtrl.ColorSets           = [new MtrlFile.AttributeSet { Name = "colorSet1", Index = 0 }];

        // Texture order and sampler flags exactly as the reference authors them:
        // norm(0), mask(1), effect/catchlight(2), id(3).
        mtrl.Textures =
        [
            new MtrlFile.Texture { Path = normal, Flags = 0 },
            new MtrlFile.Texture { Path = mask, Flags = 0 },
            new MtrlFile.Texture { Path = effectTexturePath, Flags = 0 },
            new MtrlFile.Texture { Path = id, Flags = 0 },
        ];
        mtrl.ShaderPackage.Samplers =
        [
            new Sampler { SamplerId = 0x0C5EC1F1, Flags = 0x000F836A, TextureIndex = 0 },
            new Sampler { SamplerId = 0x8A4E82B6, Flags = 0x000F836A, TextureIndex = 1 },
            new Sampler { SamplerId = 0x565F8FD8, Flags = 0x000F836A, TextureIndex = 3 },
            new Sampler { SamplerId = 0xFEA0F3D2, Flags = 0x000F8355, TextureIndex = 2 },
        ];
        mtrl.ShaderPackage.ShaderKeys =
        [
            new MtrlFile.ShaderKey { Key = 0xF52CCF05, Value = 0xA7D2FF60 },
            new MtrlFile.ShaderKey { Key = 0x40D1481E, Value = 0x337C6BC4 },
            new MtrlFile.ShaderKey { Key = 0xF886E10E, Value = 0x9A8A46F5 },
        ];

        // The reference's full constant block and values; only the tiling/scroll parameters
        // carry the user's settings — into BOTH sets, so either effect-field value behaves.
        (uint Id, float Value)[] constants =
        [
            (0x29AC0223, 0.5f), (0xD925FF32, 0.5f), (0xB7FA33E2, 1f), (0xB5545FBB, 1f),
            (0x641E0F22, 0f), (0xD26FF0AE, 0f), (0xD62BF368, 2f), (0xD07A6A65, 0f),
            (0xAD94E254, 0f), (0x39551220, 0f), (0xB61D7498, 0f), (0x5351646E, 0f),
            (0x6421DD30, 0f), (0x43345395, edit.TilingU), (0x4172EDCC, edit.TilingV),
            (0x738A241C, edit.ScrollU), (0x71CC9A45, edit.ScrollV), (0xDA3D022F, edit.TilingU),
            (0xD87BBC76, edit.TilingV), (0xEA8375A6, edit.ScrollU), (0xE8C5CBFF, edit.ScrollV),
        ];
        mtrl.ShaderPackage.Constants = constants.Select((c, i) => new MtrlFile.Constant
        {
            Id = c.Id, ByteOffset = (ushort)(i * 4), ByteSize = 4,
        }).ToArray();
        mtrl.ShaderPackage.ShaderValues = new byte[constants.Length * 4];
        for (var i = 0; i < constants.Length; i++)
            BitConverter.GetBytes(constants[i].Value).CopyTo(mtrl.ShaderPackage.ShaderValues, i * 4);

        mtrl.AdditionalData = [0x3C, 0x05, 0x00, 0x00];

        // Deep-copy the tables before mutating rows — Clone() copies them by reference.
        if (mtrl.Table is ColorTable sharedTable)
            mtrl.Table = new ColorTable(sharedTable);
        if (mtrl.DyeTable is ColorDyeTable sharedDye)
            mtrl.DyeTable = new ColorDyeTable(sharedDye);

        // The id map's G channel INTERPOLATES every colorset value between a pair's two
        // halves per texel — emissive and the effect field included. Patching a single half
        // dilutes both toward the untouched partner wherever the gear baked intermediate G
        // (observed in game: only near-full-B texels glowed, faintly). The glow must cover
        // WHOLE PAIRS — exactly how the vanilla reference authors its glowing slots, and how
        // the hair conversion sets the field on both of its pair's rows.
        if (mtrl.Table is ColorTable table)
            foreach (var pair in edit.Rows.Where(r => r is >= 0 and < ColorTable.NumRows).Select(r => r / 2).Distinct())
                foreach (var row in new[] { pair * 2, pair * 2 + 1 })
                {
                    var halves = table.RowAsHalves(row);
                    for (var c = 0; c < 3; ++c)
                        halves[8 + c] = (Half)MathF.Max(0f, edit.EffectColor[c] * edit.EffectIntensity);
                    // Effect field 1.0, matching the reference's rows that VISIBLY SCROLL —
                    // its field-2 rows carry zero scroll and look like a different (sparkle)
                    // mode on the gear shader variant. Both parameter sets get the user's
                    // values, so this is safe under either interpretation of the field.
                    halves[23] = (Half)1f;

                    // An applied stain must not override the glow color on these rows.
                    if (mtrl.DyeTable is ColorDyeTable dye)
                    {
                        var entry = dye[row];
                        entry.EmissiveColor = false;
                        dye[row] = entry;
                    }
                }

        return mtrl.Write();
    }
}
