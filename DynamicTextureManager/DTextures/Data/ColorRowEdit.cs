using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Penumbra.GameData.Files.MaterialStructs;
using Penumbra.GameData.Structs;

namespace DynamicTextureManager.DTextures.Data;

/// <summary>
/// A full edited colorset row, stored by named fields so game-struct layout changes stay a
/// mapping concern instead of data loss. Covers the Dawntrail 32-half row; legacy rows are
/// converted on capture and written back through <see cref="ColorTableRow"/>'s legacy ctor path.
/// </summary>
public sealed class ColorRowEdit
{
    public int RowIndex;

    public float[] Diffuse  = [1f, 1f, 1f];
    public float[] Specular = [1f, 1f, 1f];
    public float[] Emissive = [0f, 0f, 0f];

    public float Scalar3;
    public float Scalar7;
    public float Scalar11;
    public float SheenRate;
    public float SheenTintRate;
    public float SheenAperture;
    public float Scalar15;
    public float Roughness;
    public float Scalar17;
    public float Metalness;
    public float Anisotropy;
    public float Scalar20;
    public float SphereMapMask;
    public float Scalar22;
    public float Scalar23;
    public float TileAlpha = 1f;

    public ushort  ShaderId;
    public ushort  TileIndex;
    public ushort  SphereMapIndex;
    public float[] TileTransform = [16f, 0f, 0f, 16f];

    public enum RowDyeMode
    {
        /// <summary> Keep the source material's dye entry for this row. </summary>
        Keep,

        /// <summary> Clear the dye entry so applied stains no longer override the edited values. </summary>
        Disable,

        /// <summary> Write a custom dye entry — used to make claimed (previously unused) rows dyeable. </summary>
        Custom,
    }

    public RowDyeMode DyeMode = RowDyeMode.Keep;

    /// <summary> Dye template id for <see cref="RowDyeMode.Custom"/>. </summary>
    public ushort DyeTemplate;

    /// <summary> Dye channel (0-based, shown 1-2) for <see cref="RowDyeMode.Custom"/>. </summary>
    public byte DyeChannel;

    public bool DyeDiffuse = true;
    public bool DyeSpecular;
    public bool DyeEmissive;
    public bool DyeRoughness;
    public bool DyeMetalness;
    public bool DyeSheen;

    public static ColorRowEdit FromRow(int rowIndex, in ColorTableRow row)
        => new()
        {
            RowIndex       = rowIndex,
            Diffuse        = ToFloats(row.DiffuseColor),
            Specular       = ToFloats(row.SpecularColor),
            Emissive       = ToFloats(row.EmissiveColor),
            Scalar3        = (float)row.Scalar3,
            Scalar7        = (float)row.Scalar7,
            Scalar11       = (float)row.Scalar11,
            SheenRate      = (float)row.SheenRate,
            SheenTintRate  = (float)row.SheenTintRate,
            SheenAperture  = (float)row.SheenAperture,
            Scalar15       = (float)row.Scalar15,
            Roughness      = (float)row.Roughness,
            Scalar17       = (float)row.Scalar17,
            Metalness      = (float)row.Metalness,
            Anisotropy     = (float)row.Anisotropy,
            Scalar20       = (float)row.Scalar20,
            SphereMapMask  = (float)row.SphereMapMask,
            Scalar22       = (float)row.Scalar22,
            Scalar23       = (float)row.Scalar23,
            TileAlpha      = (float)row.TileAlpha,
            ShaderId       = row.ShaderId,
            TileIndex      = row.TileIndex,
            SphereMapIndex = row.SphereMapIndex,
            TileTransform  = [(float)row.TileTransform.UU, (float)row.TileTransform.UV, (float)row.TileTransform.VU, (float)row.TileTransform.VV],
        };

    public ColorTableRow ToRow()
        => new()
        {
            DiffuseColor   = ToColor(Diffuse),
            SpecularColor  = ToColor(Specular),
            EmissiveColor  = ToColor(Emissive),
            Scalar3        = (Half)Scalar3,
            Scalar7        = (Half)Scalar7,
            Scalar11       = (Half)Scalar11,
            SheenRate      = (Half)SheenRate,
            SheenTintRate  = (Half)SheenTintRate,
            SheenAperture  = (Half)SheenAperture,
            Scalar15       = (Half)Scalar15,
            Roughness      = (Half)Roughness,
            Scalar17       = (Half)Scalar17,
            Metalness      = (Half)Metalness,
            Anisotropy     = (Half)Anisotropy,
            Scalar20       = (Half)Scalar20,
            SphereMapMask  = (Half)SphereMapMask,
            Scalar22       = (Half)Scalar22,
            Scalar23       = (Half)Scalar23,
            TileAlpha      = (Half)TileAlpha,
            ShaderId       = ShaderId,
            TileIndex      = TileIndex,
            SphereMapIndex = SphereMapIndex,
            TileTransform  = new HalfMatrix2x2((Half)TileTransform[0], (Half)TileTransform[1], (Half)TileTransform[2], (Half)TileTransform[3]),
        };

    private static float[] ToFloats(HalfColor color)
        => [(float)color.Red, (float)color.Green, (float)color.Blue];

    private static HalfColor ToColor(float[] values)
        => new((Half)values[0], (Half)values[1], (Half)values[2]);

    public JObject Serialize()
        => new()
        {
            ["RowIndex"]       = RowIndex,
            ["Diffuse"]        = new JArray(Diffuse.Cast<object>()),
            ["Specular"]       = new JArray(Specular.Cast<object>()),
            ["Emissive"]       = new JArray(Emissive.Cast<object>()),
            ["Scalar3"]        = Scalar3,
            ["Scalar7"]        = Scalar7,
            ["Scalar11"]       = Scalar11,
            ["SheenRate"]      = SheenRate,
            ["SheenTintRate"]  = SheenTintRate,
            ["SheenAperture"]  = SheenAperture,
            ["Scalar15"]       = Scalar15,
            ["Roughness"]      = Roughness,
            ["Scalar17"]       = Scalar17,
            ["Metalness"]      = Metalness,
            ["Anisotropy"]     = Anisotropy,
            ["Scalar20"]       = Scalar20,
            ["SphereMapMask"]  = SphereMapMask,
            ["Scalar22"]       = Scalar22,
            ["Scalar23"]       = Scalar23,
            ["TileAlpha"]      = TileAlpha,
            ["ShaderId"]       = ShaderId,
            ["TileIndex"]      = TileIndex,
            ["SphereMapIndex"] = SphereMapIndex,
            ["TileTransform"]  = new JArray(TileTransform.Cast<object>()),
            ["DyeMode"]        = DyeMode.ToString(),
            ["DyeTemplate"]    = DyeTemplate,
            ["DyeChannel"]     = DyeChannel,
            ["DyeDiffuse"]     = DyeDiffuse,
            ["DyeSpecular"]    = DyeSpecular,
            ["DyeEmissive"]    = DyeEmissive,
            ["DyeRoughness"]   = DyeRoughness,
            ["DyeMetalness"]   = DyeMetalness,
            ["DyeSheen"]       = DyeSheen,
        };

    public static ColorRowEdit Load(JObject json)
        => new()
        {
            RowIndex       = json["RowIndex"]?.ToObject<int>() ?? 0,
            Diffuse        = LoadFloats(json["Diffuse"], [1f, 1f, 1f]),
            Specular       = LoadFloats(json["Specular"], [1f, 1f, 1f]),
            Emissive       = LoadFloats(json["Emissive"], [0f, 0f, 0f]),
            Scalar3        = json["Scalar3"]?.ToObject<float>() ?? 0f,
            Scalar7        = json["Scalar7"]?.ToObject<float>() ?? 0f,
            Scalar11       = json["Scalar11"]?.ToObject<float>() ?? 0f,
            SheenRate      = json["SheenRate"]?.ToObject<float>() ?? 0f,
            SheenTintRate  = json["SheenTintRate"]?.ToObject<float>() ?? 0f,
            SheenAperture  = json["SheenAperture"]?.ToObject<float>() ?? 0f,
            Scalar15       = json["Scalar15"]?.ToObject<float>() ?? 0f,
            Roughness      = json["Roughness"]?.ToObject<float>() ?? 0f,
            Scalar17       = json["Scalar17"]?.ToObject<float>() ?? 0f,
            Metalness      = json["Metalness"]?.ToObject<float>() ?? 0f,
            Anisotropy     = json["Anisotropy"]?.ToObject<float>() ?? 0f,
            Scalar20       = json["Scalar20"]?.ToObject<float>() ?? 0f,
            SphereMapMask  = json["SphereMapMask"]?.ToObject<float>() ?? 0f,
            Scalar22       = json["Scalar22"]?.ToObject<float>() ?? 0f,
            Scalar23       = json["Scalar23"]?.ToObject<float>() ?? 0f,
            TileAlpha      = json["TileAlpha"]?.ToObject<float>() ?? 1f,
            ShaderId       = json["ShaderId"]?.ToObject<ushort>() ?? 0,
            TileIndex      = json["TileIndex"]?.ToObject<ushort>() ?? 0,
            SphereMapIndex = json["SphereMapIndex"]?.ToObject<ushort>() ?? 0,
            TileTransform  = LoadFloats(json["TileTransform"], [16f, 0f, 0f, 16f]),
            DyeMode        = LoadDyeMode(json),
            DyeTemplate    = json["DyeTemplate"]?.ToObject<ushort>() ?? 0,
            DyeChannel     = json["DyeChannel"]?.ToObject<byte>() ?? 0,
            DyeDiffuse     = json["DyeDiffuse"]?.ToObject<bool>() ?? true,
            DyeSpecular    = json["DyeSpecular"]?.ToObject<bool>() ?? false,
            DyeEmissive    = json["DyeEmissive"]?.ToObject<bool>() ?? false,
            DyeRoughness   = json["DyeRoughness"]?.ToObject<bool>() ?? false,
            DyeMetalness   = json["DyeMetalness"]?.ToObject<bool>() ?? false,
            DyeSheen       = json["DyeSheen"]?.ToObject<bool>() ?? false,
        };

    private static RowDyeMode LoadDyeMode(JObject json)
    {
        if (Enum.TryParse<RowDyeMode>(json["DyeMode"]?.ToObject<string>(), out var mode))
            return mode;

        // Files from before the dye modes only had the boolean.
        return json["DisableDye"]?.ToObject<bool>() == true ? RowDyeMode.Disable : RowDyeMode.Keep;
    }

    private static float[] LoadFloats(JToken? token, float[] fallback)
    {
        if (token is not JArray array || array.Count != fallback.Length)
            return fallback;

        return array.Select(t => t.ToObject<float>()).ToArray();
    }
}
