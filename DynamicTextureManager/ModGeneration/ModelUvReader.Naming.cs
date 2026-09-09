using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Interop;
using IService = Luna.IService;
using Penumbra.GameData.Files;

namespace DynamicTextureManager.ModGeneration;

// The naming rules of body and face skin materials: which game paths count as skin,
// how body material names substitute the wearer race, and the SmallClothes model set
// (e0000) that IS the nude body — the game has no body model of its own.
public sealed partial class ModelUvReader
{
    /// <summary>
    /// Body skin materials live under chara/human but have NO model of their own — the game
    /// has no nude body model. The SmallClothes equipment models (e0000) ARE the nude body;
    /// worn gear models merely embed the skin patches they expose.
    /// </summary>
    private static readonly Regex BodySkinMaterialPattern =
        new(@"^chara/human/(c\d{4})/obj/body/b\d{4}/material/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The recorded model of a body skin source, carrying the race code of the SmallClothes
    /// set to load. NOT derivable from the material path: body-mod families deliberately use
    /// foreign race codes in their material paths (e.g. bibo's c0101-pathed material on a
    /// c0201 female body), so the race must come from the models actually worn.
    /// </summary>
    private static readonly Regex BodyTopModelPattern =
        new(@"^chara/equipment/e0000/model/(c\d{4})e0000_top\.mdl$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsBodySkinMaterial(string materialGamePath)
        => BodySkinMaterialPattern.IsMatch(materialGamePath);

    /// <summary>
    /// Face skin materials. The face keeps its own model (unlike the body), but its material
    /// path carries the correct race/face ids — faces are per-character, no race substitution.
    /// </summary>
    private static readonly Regex FaceSkinMaterialPattern =
        new(@"^chara/human/(c\d{4})/obj/face/(f\d{4})/material/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsFaceSkinMaterial(string materialGamePath)
        => FaceSkinMaterialPattern.IsMatch(materialGamePath);

    /// <summary> Body material file name (mt_cXXXXbYYYY_*.mtrl) → its conventional game path; the race/body codes live in the name itself. </summary>
    private static readonly Regex BodyMaterialNamePattern =
        new(@"^mt_(c\d{4})(b\d{4})_", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MaterialVariantPattern =
        new(@"/material/(v\d{4})/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? BodyMaterialGamePath(string materialFileName, string variant)
    {
        var match = BodyMaterialNamePattern.Match(materialFileName);
        return match.Success
            ? $"chara/human/{match.Groups[1].Value}/obj/body/{match.Groups[2].Value}/material/{variant}/{materialFileName}"
            : null;
    }

    /// <summary>
    /// The game substitutes the wearer's race code into body material names referenced by
    /// models — a model authored as c0101 resolves its "/mt_c0101b0001_bibo.mtrl" to
    /// mt_c0201b0001_bibo.mtrl on a c0201 body. Matching must do the same, or the torso of a
    /// mixed-authoring body mod never matches its own material.
    /// </summary>
    public static string SubstituteBodyRace(string materialFileName, string race)
        => BodyMaterialNamePattern.IsMatch(materialFileName) ? $"mt_{race}{materialFileName[8..]}" : materialFileName;

    /// <summary> The race code in a body skin material's path — a fallback only, see <see cref="BodyTopModelPattern"/>. </summary>
    public static string BodyMaterialRace(string materialGamePath)
    {
        var match = BodySkinMaterialPattern.Match(materialGamePath);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary> The SmallClothes model set (chest, legs, hands, feet) of a body race code. </summary>
    public static string[] BodyModelSetForRace(string race)
        =>
        [
            $"chara/equipment/e0000/model/{race}e0000_top.mdl",
            $"chara/equipment/e0000/model/{race}e0000_dwn.mdl",
            $"chara/equipment/e0000/model/{race}e0000_glv.mdl",
            $"chara/equipment/e0000/model/{race}e0000_sho.mdl",
        ];
}
