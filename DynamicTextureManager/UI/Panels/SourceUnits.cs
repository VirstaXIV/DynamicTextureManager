using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration;

namespace DynamicTextureManager.UI.Panels;

/// <summary> One selected source unit: a model (gear piece, the body, the hair) with all its materials. </summary>
public sealed record SourceUnit(string Key, string Label, SourcePath Primary, IReadOnlyList<SourcePath> Materials);

/// <summary>
/// Sources are MODEL units — the user picks and edits pieces, not raw materials. This maps
/// the flat per-material source list into those units and names them by their slot/type.
/// </summary>
public static class SourceUnits
{
    public static readonly System.Text.RegularExpressions.Regex HairModelPattern =
        new(@"^chara/human/c\d{4}/obj/hair/h\d{4}/model/",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary> Tail models — hair-family on furred races (Miqo'te/Hrothgar tails are hair.shpk, colored by the customize hair colors); Au Ra scale tails are skin.shpk and are filtered out by the material-kind check instead. </summary>
    public static readonly System.Text.RegularExpressions.Regex TailModelPattern =
        new(@"^chara/human/c\d{4}/obj/tail/t\d{4}/model/",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary> Viera ear models (Miqo'te ears live inside the hair models and need no own entry). </summary>
    public static readonly System.Text.RegularExpressions.Regex EarModelPattern =
        new(@"^chara/human/c\d{4}/obj/zear/z\d{4}/model/",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Whether a model can carry hair-shader materials the hair pipeline (Shine, Animated
    /// Effect) applies to: the hairstyle itself, tails, and Viera ears. Which of a model's
    /// materials actually qualify is decided per material by its shader kind.
    /// </summary>
    public static bool IsHairFamilyModel(string mdlGamePath)
        => HairModelPattern.IsMatch(mdlGamePath) || TailModelPattern.IsMatch(mdlGamePath) || EarModelPattern.IsMatch(mdlGamePath);

    /// <summary> The selected source materials grouped into model units, in add order. </summary>
    public static List<SourceUnit> Of(SourceRef source)
        => source.Materials
            .GroupBy(m => m.MdlGamePath.Length > 0 ? m.MdlGamePath : m.GamePath, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var primary = g.FirstOrDefault(m => !m.Overlay) ?? g.First();
                return new SourceUnit(g.Key, $"{UnitLabel(primary.MdlGamePath, primary.GamePath)}: {primary.Label}",
                    primary, g.ToList());
            })
            .ToList();

    /// <summary> The equipment/accessory slot of a model, from its file name suffix. </summary>
    public static (int Order, string Label) GearSlot(string mdlGamePath)
    {
        if (mdlGamePath.Contains("/weapon/", StringComparison.OrdinalIgnoreCase))
            return (10, "Weapon");

        var name   = Path.GetFileNameWithoutExtension(mdlGamePath);
        var suffix = name.Length >= 3 ? name[^3..].ToLowerInvariant() : string.Empty;
        return suffix switch
        {
            "met" => (0, "Head"),
            "top" => (1, "Body"),
            "glv" => (2, "Hands"),
            "dwn" => (3, "Legs"),
            "sho" => (4, "Feet"),
            "ear" => (5, "Earrings"),
            "nek" => (6, "Necklace"),
            "wrs" => (7, "Bracelets"),
            "rir" => (8, "Ring (Right)"),
            "ril" => (9, "Ring (Left)"),
            _     => (11, "Other"),
        };
    }

    /// <summary> The kind of unit a source material belongs to, for the model-based lists. </summary>
    public static string UnitLabel(string mdlGamePath, string materialGamePath)
    {
        if (ModelUvReader.IsBodySkinMaterial(materialGamePath))
            return "Body";
        if (HairModelPattern.IsMatch(mdlGamePath))
            return "Hair";
        if (mdlGamePath.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase))
            return "Face";
        if (mdlGamePath.Contains("/obj/tail/", StringComparison.OrdinalIgnoreCase))
            return "Tail";
        if (mdlGamePath.Contains("/obj/zear/", StringComparison.OrdinalIgnoreCase))
            return "Ears";
        if (mdlGamePath.Contains("/human/", StringComparison.OrdinalIgnoreCase))
            return "Character";
        return GearSlot(mdlGamePath).Label;
    }
}
