using System;
using System.Linq;
using Lumina.Data.Parsing;
using DynamicTextureManager.DTextures.Data;
using Penumbra.GameData.Files;
using Penumbra.GameData.Files.MaterialStructs;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Builds the animated-highlight replacement for a hair material: a characterscroll.shpk
/// material whose colorset pair 16 splits the hair into an emissive scrolling effect (row A,
/// where the highlights are) and the base hair color (row B), plus the four companion
/// textures the shader samples. The material structure replicates a community reference
/// (offline-verified byte-identical reconstruction); the colorset rows and animation
/// constants are parameterized from <see cref="AnimatedHairEdit"/>.
/// </summary>
public static class AnimatedHairBuilder
{
    /// <summary> Game paths of the generated companion textures, derived from the material's own path. </summary>
    public readonly record struct TexturePaths(string Normal, string Mask, string Id, string Effect);

    public static TexturePaths PathsFor(string materialGamePath)
    {
        // chara/.../hXXXX/material/v0001/mt_..._hir_a.mtrl -> chara/.../hXXXX/texture/dtmfx_..._*.tex
        var materialIndex = materialGamePath.LastIndexOf("/material/", StringComparison.OrdinalIgnoreCase);
        var directory     = materialIndex >= 0 ? materialGamePath[..materialIndex] + "/texture/" : "chara/common/texture/";
        var name          = materialGamePath[(materialGamePath.LastIndexOf('/') + 1)..];
        if (name.StartsWith("mt_", StringComparison.OrdinalIgnoreCase))
            name = name[3..];
        if (name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];

        var stem = $"{directory}dtmfx_{name}";
        return new TexturePaths($"{stem}_norm.tex", $"{stem}_mask.tex", $"{stem}_id.tex", $"{stem}_effect.tex");
    }

    private static readonly System.Text.RegularExpressions.Regex HairMaterialName =
        new(@"^mt_c\d{4}h\d{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary> Whether a model material name looks like a standard hair-family material. </summary>
    public static bool IsHairMaterialName(string fileName)
        => HairMaterialName.IsMatch(fileName);

    /// <summary>
    /// The game path of a hairstyle sibling material: the game resolves ALL of a model's
    /// materials into the MODEL's own material folder (verified against real mod layouts —
    /// e.g. mt_c0201h0179_hir_a.mtrl lives under .../hair/h0164/material/v0001/ when the
    /// h0164 model references it), so the sibling shares the primary material's directory
    /// and only the file name differs. Null for names that are not hair materials.
    /// </summary>
    public static string? SiblingMaterialGamePath(string primaryMaterialGamePath, string fileName)
    {
        if (!IsHairMaterialName(fileName))
            return null;

        var slash = primaryMaterialGamePath.LastIndexOf('/');
        return slash < 0 ? null : $"{primaryMaterialGamePath[..(slash + 1)]}{fileName}";
    }

    // Colorset row templates from the reference material (raw half bits, 32 halves per row).
    // Row 16A: the effect row — diffuse patched from the edit's highlight color, emissive +
    // spec from the effect color.
    private static readonly ushort[] EffectRowTemplate =
    [
        0x0000, 0x0000, 0x0000, 0x3C00, 0x2EA9, 0x0000, 0x0000, 0x0000, 0x3640, 0x2DAD, 0x1837, 0x3C00,
        0x3800, 0x3C00, 0x4500, 0x0000, 0x34CD, 0x0000, 0x0000, 0x0000, 0x0000, 0x3C00, 0x0000, 0x4000,
        0x4700, 0x2000, 0x3C00, 0x0000, 0x5FD0, 0x0000, 0x0000, 0x5FD0,
    ];

    // Row 16B: the base hair row — diffuse patched from the edit's base color.
    private static readonly ushort[] BaseRowTemplate =
    [
        0x0000, 0x0000, 0x0000, 0x3C00, 0x31F5, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x3C00,
        0x3266, 0x2E66, 0x4500, 0x0000, 0x3266, 0x0000, 0x3800, 0x0000, 0x0000, 0x3C00, 0x0000, 0x4000,
        0x0000, 0x2000, 0x3C00, 0x0000, 0x4C00, 0x0000, 0x0000, 0x4C00,
    ];

    /// <summary>
    /// The animated conversion's colorset: the base hair row everywhere (any stray id value
    /// renders hair-colored — and colorset-decal edge interpolation between a claimed pair
    /// and pair 16 crosses only base-colored rows), pair 16 split into the effect row (16A)
    /// and the base row (16B). Also the UI's stand-in table for seeding decal rows — the
    /// source hair.shpk material has no colorset to seed from.
    /// </summary>
    public static ColorTable BuildColorTable(AnimatedHairEdit edit)
    {
        var table = new ColorTable();
        for (var r = 0; r < ColorTable.NumRows; r++)
        {
            WriteRow(table, r, BaseRowTemplate);
            PatchColor(table, r, 0, edit.BaseColor, 1f);
        }

        WriteRow(table, 30, EffectRowTemplate);
        PatchColor(table, 30, 0, edit.HighlightColor, 1f);                        // highlight-area hair diffuse
        PatchColor(table, 30, 8, edit.EffectColor, edit.EffectIntensity);        // emissive
        PatchColor(table, 30, 4, edit.EffectColor, edit.EffectIntensity * 0.27f); // spec tint
        return table;
    }

    /// <summary>
    /// Transform a hair material into the animated characterscroll variant. The source can be
    /// the vanilla or a modded hair.shpk mtrl — everything shader-related is replaced
    /// wholesale, so only the file header/version carries over. The source instance is never
    /// mutated. Returned unparsed so callers can apply colorset row edits (decal slots)
    /// before writing.
    /// </summary>
    public static MtrlFile BuildMaterialFile(MtrlFile sourceMtrl, AnimatedHairEdit edit, TexturePaths paths)
    {
        var mtrl = sourceMtrl.Clone();

        mtrl.ShaderPackage.Name  = "characterscroll.shpk";
        mtrl.ShaderPackage.Flags = 0x0000001C;
        mtrl.Textures =
        [
            new MtrlFile.Texture { Path = paths.Normal, Flags = 0 },
            new MtrlFile.Texture { Path = paths.Mask,   Flags = 0 },
            new MtrlFile.Texture { Path = paths.Id,     Flags = 0 },
            new MtrlFile.Texture { Path = paths.Effect, Flags = 0 },
        ];
        mtrl.ShaderPackage.Samplers =
        [
            new Sampler { SamplerId = 0x0C5EC1F1, Flags = 0x000F8355, TextureIndex = 0 },
            new Sampler { SamplerId = 0x8A4E82B6, Flags = 0x000F8355, TextureIndex = 1 },
            new Sampler { SamplerId = 0x565F8FD8, Flags = 0x000F8015, TextureIndex = 2 },
            new Sampler { SamplerId = 0xFEA0F3D2, Flags = 0x000F8355, TextureIndex = 3 },
        ];
        mtrl.ShaderPackage.ShaderKeys =
        [
            new MtrlFile.ShaderKey { Key = 0xF52CCF05, Value = 0xA7D2FF60 },
            new MtrlFile.ShaderKey { Key = 0xF886E10E, Value = 0x9A8A46F5 },
        ];

        // Scalar constants in the reference cbuffer order. Shader-verified (SM5 disasm of
        // the one characterscroll pixel shader binding g_SamplerCatchlight): the effect UV
        // is texcoord × (tilingU, tilingV) + instanceTime × (scrollU, scrollV), with the
        // colorset row's effect-channel field selecting parameter set A
        // (0x43345395/0x4172EDCC tiling, 0x738A241C/0x71CC9A45 scroll) or set B
        // (0xDA3D022F/0xD87BBC76 tiling, 0xEA8375A6/0xE8C5CBFF scroll). Our rows select
        // set B, but both sets get the same user values so every channel behaves.
        (uint Id, float Value)[] constants =
        [
            (0x29AC0223, 0.5f), (0xD925FF32, 0.5f), (0xB7FA33E2, 1f), (0xB5545FBB, 1f),
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
        mtrl.UvSets    = [new MtrlFile.AttributeSet { Name = "map1", Index = 0 }];
        mtrl.ColorSets = [new MtrlFile.AttributeSet { Name = "colorSet1", Index = 0 }];

        mtrl.Table    = BuildColorTable(edit);
        mtrl.DyeTable = new ColorDyeTable();

        return mtrl;
    }

    private static void WriteRow(ColorTable table, int row, ushort[] template)
    {
        var bytes = table.RowAsBytes(row);
        for (var i = 0; i < template.Length && i * 2 + 1 < bytes.Length; i++)
        {
            bytes[i * 2]     = (byte)(template[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)(template[i] >> 8);
        }
    }

    private static void PatchColor(ColorTable table, int row, int halfIndex, float[] rgb, float scale)
    {
        var halves = table.RowAsHalves(row);
        for (var c = 0; c < 3 && halfIndex + c < halves.Length; c++)
            halves[halfIndex + c] = (Half)MathF.Max(0f, rgb[c] * scale);
    }

    /// <summary>
    /// The replacement normal: source RG strand detail kept, the cutout alpha moved into B
    /// (the character-shader family reads opacity from normal B), alpha forced opaque.
    /// Operates on an RGBA buffer copy.
    /// </summary>
    public static byte[] BuildNormalRgba(byte[] compositedNormal)
    {
        var result = (byte[])compositedNormal.Clone();
        for (var i = 0; i + 3 < result.Length; i += 4)
        {
            result[i + 2] = result[i + 3]; // B := cutout alpha
            result[i + 3] = 255;
        }

        return result;
    }

    /// <summary>
    /// The id map routing the highlight distribution into colorset pair 16: R=255 selects the
    /// pair, G = the hair normal's blue channel (0 = base row B, 255 = effect row A) — so the
    /// authored highlights AND every highlight edit baked into the composited normal decide
    /// where the effect appears.
    /// </summary>
    public static byte[] BuildIdRgba(byte[] compositedNormal)
    {
        var result = new byte[compositedNormal.Length];
        for (var i = 0; i + 3 < result.Length; i += 4)
        {
            result[i]     = 255;
            result[i + 1] = compositedNormal[i + 2]; // G := highlight blend (normal B)
            result[i + 2] = 255;
            result[i + 3] = 255;
        }

        return result;
    }

    public const int MaskSize = 16;

    /// <summary>
    /// The reference's mask: a small pure-white tile — flat shading, kept as the fallback
    /// when the hair mask cannot be composited.
    /// </summary>
    public static byte[] BuildMaskRgba()
    {
        var result = new byte[MaskSize * MaskSize * 4];
        Array.Fill(result, (byte)255);
        return result;
    }

    /// <summary>
    /// The character-shader mask derived from the composited HAIR mask, so the conversion
    /// keeps the original per-strand shading instead of the reference's flat white tile.
    /// Hair mask channels are R=spec power, G=roughness, B=SSS, A=ambient occlusion.
    /// Character-family mask semantics as established in game: B MULTIPLIES THE DIFFUSE
    /// (proven empirically — B=0 rendered the hair pure black, B=255 flat bright), R dims
    /// the specular (cavity occlusion), G is roughness. Any ABSOLUTE use of the AO in B
    /// tints the whole style grey (hair AO averages well under 1, and the real hair shader
    /// clearly does not multiply diffuse by it — unconverted white hair stays white), so
    /// the AO is NORMALIZED around its own mean: a typical strand keeps FULL diffuse
    /// brightness, only crevices darker than typical shade down. Squared into the shader's
    /// linear domain from the display-domain curve.
    /// </summary>
    public static byte[] BuildCharMaskRgba(byte[] compositedHairMask)
    {
        long aoSum = 0;
        for (var i = 3; i < compositedHairMask.Length; i += 4)
            aoSum += compositedHairMask[i];
        var mean = Math.Max(1f, aoSum / (compositedHairMask.Length / 4f)) / 255f;

        var result = new byte[compositedHairMask.Length];
        for (var i = 0; i + 3 < result.Length; i += 4)
        {
            var relative = MathF.Min(1f, compositedHairMask[i + 3] / 255f / mean);
            var display  = MathF.Pow(relative, 0.75f);
            result[i]     = compositedHairMask[i + 3]; // R := AO (spec occlusion)
            result[i + 1] = compositedHairMask[i + 1]; // G := roughness
            result[i + 2] = (byte)Math.Clamp((int)MathF.Round(display * display * 255f), 0, 255);
            result[i + 3] = 255;
        }

        return result;
    }

    /// <summary> Built-in black/white effect patterns scrolled across the highlights. </summary>
    public enum HairEffectPattern
    {
        Shimmer = 0,
        Flames  = 1,
        Streaks = 2,
        Waves   = 3,
        Sparks  = 4,
    }

    public static string PatternLabel(HairEffectPattern pattern)
        => pattern switch
        {
            HairEffectPattern.Flames  => "Flames",
            HairEffectPattern.Streaks => "Streaks",
            HairEffectPattern.Waves   => "Waves",
            HairEffectPattern.Sparks  => "Sparks",
            _                         => "Shimmer",
        };

    public const int PatternSize = 512;

    /// <summary>
    /// Generate a built-in effect pattern, deterministic and TILEABLE (the shader wraps and
    /// scrolls it — a non-tiling pattern would sweep a visible seam through the hair).
    /// Grayscale RGBA; white shows the effect color.
    /// </summary>
    public static byte[] GeneratePattern(HairEffectPattern pattern, int size = PatternSize)
    {
        // Any noise becomes tileable by cross-blending the four period-shifted copies —
        // per-axis periods, since most patterns stretch U and V differently.
        float TileableFbm(int seed, float x, float y, float periodX, float periodY, int octaves)
        {
            var fx = x % periodX;
            var fy = y % periodY;
            var wx = fx / periodX;
            var wy = fy / periodY;
            float S(float ox, float oy) => ProceduralMasks.Fbm(seed, new System.Numerics.Vector2(fx + ox, fy + oy), octaves);
            return S(0, 0) * (1 - wx) * (1 - wy)
                 + S(-periodX, 0) * wx * (1 - wy)
                 + S(0, -periodY) * (1 - wx) * wy
                 + S(-periodX, -periodY) * wx * wy;
        }

        static float Smooth(float a, float b, float t)
            => t <= a ? 0f : t >= b ? 1f : (t - a) / (b - a) is var s ? s * s * (3f - 2f * s) : 0f;

        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var u = x / (float)size;
            var v = y / (float)size;
            var value = pattern switch
            {
                // Narrow elongated vertical licks with ragged bright tips.
                HairEffectPattern.Flames => Smooth(0.5f, 0.7f, TileableFbm(211, u * 10f, v * 3f, 10f, 3f, 3)),

                // Diagonal bands, edges jittered by noise.
                HairEffectPattern.Streaks => Smooth(0.35f, 0.85f,
                    (MathF.Sin((u + v) * MathF.Tau * 5f + TileableFbm(97, u * 4f, v * 4f, 4f, 4f, 2) * 3f) + 1f) * 0.5f),

                // Soft horizontal wave bands.
                HairEffectPattern.Waves => Smooth(0.25f, 0.9f,
                    (MathF.Sin(v * MathF.Tau * 7f + TileableFbm(1543, u * 3f, v * 3f, 3f, 3f, 2) * 2.5f) + 1f) * 0.5f),

                // Scattered bright specks.
                HairEffectPattern.Sparks => Smooth(0.6f, 0.75f, TileableFbm(4099, u * 26f, v * 26f, 26f, 26f, 2)),

                // Soft drifting glow patches.
                _ => Smooth(0.38f, 0.72f, TileableFbm(7919, u * 5f, v * 3f, 5f, 3f, 3)),
            };

            var b     = (byte)Math.Clamp((int)(value * 255f), 0, 255);
            var index = (y * size + x) * 4;
            rgba[index] = rgba[index + 1] = rgba[index + 2] = b;
            rgba[index + 3] = 255;
        }

        return rgba;
    }
}
