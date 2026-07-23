using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.DTextures.Data;

/// <summary>
/// One operation in a texture's layer stack. Only decal layers exist for now; the loader
/// skips unknown layer types with a warning so future layer kinds stay forward-compatible.
/// </summary>
public abstract class TextureLayer
{
    public bool Enabled = true;

    public abstract string LayerType { get; }

    protected abstract void SerializeInto(JObject json);

    public JObject Serialize()
    {
        var ret = new JObject
        {
            ["LayerType"] = LayerType,
            ["Enabled"]   = Enabled,
        };
        SerializeInto(ret);
        return ret;
    }

    public static TextureLayer? Load(JObject json)
    {
        var type = json["LayerType"]?.ToObject<string>();
        TextureLayer? ret = type switch
        {
            DecalLayer.Type => DecalLayer.LoadDecal(json),
            _               => null,
        };
        if (ret == null)
        {
            DynamicTextureManager.Log.Warning($"Skipped unknown texture layer type \"{type}\".");
            return null;
        }

        ret.Enabled = json["Enabled"]?.ToObject<bool>() ?? true;
        return ret;
    }

    public static List<TextureLayer> LoadList(JToken? token)
        => token is JArray array
            ? array.OfType<JObject>().Select(Load).OfType<TextureLayer>().ToList()
            : [];
}

/// <summary> How a decal adjusts the material's surface finish inside its footprint. </summary>
public enum DecalFinishMode
{
    Keep   = 0,
    Matte  = 1,
    Glossy = 2,
    Custom = 3,
}

/// <summary> A decal image stamped onto a target texture at a UV position with scale, rotation and opacity. </summary>
public sealed class DecalLayer : TextureLayer
{
    public const string Type = "Decal";

    /// <summary> Id of the decal image in the decal library. </summary>
    public Guid DecalId;

    /// <summary> Center position in normalized UV space (0..1). </summary>
    public float PosU = 0.5f;

    /// <summary> Center position in normalized UV space (0..1). </summary>
    public float PosV = 0.5f;

    /// <summary> Scale relative to the target texture's width/height. </summary>
    public float ScaleX = 0.25f;

    /// <summary> Scale relative to the target texture's width/height. </summary>
    public float ScaleY = 0.25f;

    public float RotationDeg;

    /// <summary> Mirror the decal image along its own horizontal/vertical axis. </summary>
    public bool FlipX;

    public bool FlipY;

    public float Opacity = 1f;

    /// <summary>
    /// Colorset-decal mode for ID maps of colorset-driven materials (no diffuse texture):
    /// instead of alpha-blending colors, opaque decal pixels remap the ID texels to
    /// automatically claimed colorset rows, whose row colors then render the decal.
    /// </summary>
    public bool IdRemap;

    /// <summary> Quantization cap: the decal image is reduced to at most this many colors. </summary>
    public int MaxColors = 6;

    /// <summary> Extracted palette, packed Rgba32 (0xAABBGGRR); order matches <see cref="PaletteRows"/>. </summary>
    public List<uint> PaletteColors = [];

    /// <summary> Claimed colorset half-row indices (0-31); PaletteRows[i] renders PaletteColors[i]. </summary>
    public List<int> PaletteRows = [];

    /// <summary>
    /// Recolor mode for diffuse-target decals (skin, legacy gear): decal pixels nearest-map
    /// to <see cref="PaletteColors"/> and render <see cref="TintColors"/>[i] instead, baked
    /// into the texture at composite time — the diffuse counterpart of colorset row recolors.
    /// </summary>
    public bool TintEnabled;

    /// <summary> Replacement colors, packed Rgba32; index-parallel to <see cref="PaletteColors"/>. </summary>
    public List<uint> TintColors = [];

    /// <summary> Whether the tint is active and consistent enough to apply. </summary>
    public bool HasTint
        => TintEnabled && PaletteColors.Count > 0 && TintColors.Count == PaletteColors.Count;

    /// <summary> Runtime-only allocation failure shown in the UI; the layer auto-disables when set. </summary>
    public string? RowError;

    /// <summary> Decal pixels with alpha at or above this threshold are remapped in <see cref="IdRemap"/> mode. </summary>
    public float AlphaThreshold = 0.5f;

    /// <summary> The threshold as an alpha byte, floored at 1 so fully transparent pixels never pass. </summary>
    public byte AlphaThresholdByte
        => (byte)System.Math.Clamp((int)System.Math.Round(AlphaThreshold * 255f), 1, 255);

    /// <summary>
    /// All textures of a material are related: a printed-on decal usually wants the cloth
    /// bump detail under it smoothed away. Blends the material's normal map toward flat
    /// inside the decal footprint (0 = leave the normal map untouched).
    /// </summary>
    public float NormalSmooth;

    /// <summary>
    /// Surface finish inside the decal footprint. Written into the material's mask map, and —
    /// for id-remap decals — into the claimed colorset rows' roughness/specular, which is what
    /// actually dominates perceived shine on colorset-driven gear.
    /// </summary>
    public DecalFinishMode Finish = DecalFinishMode.Keep;

    /// <summary> Roughness for <see cref="DecalFinishMode.Custom"/> (0 = mirror-glossy, 1 = fully matte). </summary>
    public float FinishRoughness = 0.5f;

    /// <summary> Specular multiplier for <see cref="DecalFinishMode.Custom"/> applied to the authored row specular. </summary>
    public float FinishSpecScale = 1f;

    /// <summary> Size of the material-effect footprint relative to the decal (1 = exactly the decal shape). </summary>
    public float EffectScale = 1f;

    /// <summary> Whether this layer also edits the material's sibling textures (normal/mask). </summary>
    public bool HasMaterialEffects
        => NormalSmooth > 0f || Finish != DecalFinishMode.Keep;

    /// <summary> Whether the finish setting wants a mask-map write. </summary>
    public bool WantsMaskEffect
        => Finish != DecalFinishMode.Keep;

    /// <summary>
    /// Surface mode: instead of a rectangle in UV space, the decal is projected onto the 3D
    /// mesh from an anchor point placed on the model. It conforms to the surface, stretches
    /// with the actual geometry and continues across UV seams.
    /// </summary>
    public bool Surface;

    /// <summary> Projection anchor on the mesh, in bind-pose model space. </summary>
    public float AnchorX, AnchorY, AnchorZ;

    /// <summary> Projection direction (surface normal at the anchor), bind-pose model space. </summary>
    public float NormalX, NormalY = 1f, NormalZ;

    /// <summary> Decal size on the surface in meters, for <see cref="Surface"/> mode. </summary>
    public float WorldWidth = 0.1f;

    /// <summary> Decal size on the surface in meters, for <see cref="Surface"/> mode. </summary>
    public float WorldHeight = 0.1f;

    /// <summary> Connected mesh part the anchor was stamped on; the bake stays on it when <see cref="SurfaceLimitToPart"/> is set. </summary>
    public int SurfacePart = -1;

    /// <summary> Keep the projection on the clicked mesh part instead of everything within reach (linings, straps). </summary>
    public bool SurfaceLimitToPart = true;

    /// <summary> Attribute mask of visible submeshes captured at stamp time — hidden variant geometry is not baked. </summary>
    public uint SurfaceAttributes = uint.MaxValue;

    /// <summary> Enabled shape keys captured at stamp time, so the bake targets the same morphed surface that was clicked. </summary>
    public uint SurfaceShapes;

    /// <summary>
    /// Raw decal lifted out of the source id map instead of stamped from the library: its
    /// pairs are the gear's own authored slots and the bake erases the original footprint
    /// before restamping, so the decal can be moved and its rows recolored.
    /// </summary>
    public bool Extracted;

    /// <summary> Original footprint rectangle in normalized UV space (top-left corner); negative U means unknown. </summary>
    public float SourceU = -1f;

    public float SourceV;

    /// <summary> Original footprint extent in normalized UV space. </summary>
    public float SourceUW;

    public float SourceUH;

    /// <summary> 0-based colorset pair the erase fills the footprint's R channel with (the dominant surrounding pair). </summary>
    public int FillPair = -1;

    /// <summary> G (A/B blend) value the erase fills the footprint with; -1 leaves G untouched. </summary>
    public int FillBlend = -1;

    /// <summary>
    /// Stamping also writes the id map's G channel from the decal pixel's alpha. Used by
    /// extracted decals relocated onto their own claimed pairs: the source content lived on
    /// specific A/B halves of shared pairs, so the stamp must steer the blend onto the new
    /// pair's A row instead of inheriting whatever the garment baked there.
    /// </summary>
    public bool WriteBlendFromAlpha;

    /// <summary>
    /// The texture source captured BEFORE this extraction redirected it to a cleaned copy
    /// (empty = vanilla). Removing the extraction restores this and deletes the copy, so the
    /// source returns to the base mod. Null on non-extracted layers.
    /// </summary>
    public string? PreExtractionSource;

    public override string LayerType
        => Type;

    protected override void SerializeInto(JObject json)
    {
        json["DecalId"]        = DecalId;
        json["PosU"]           = PosU;
        json["PosV"]           = PosV;
        json["ScaleX"]         = ScaleX;
        json["ScaleY"]         = ScaleY;
        json["RotationDeg"]    = RotationDeg;
        json["FlipX"]          = FlipX;
        json["FlipY"]          = FlipY;
        json["Opacity"]        = Opacity;
        json["IdRemap"]        = IdRemap;
        json["MaxColors"]      = MaxColors;
        json["PaletteColors"]  = new JArray(PaletteColors);
        json["PaletteRows"]    = new JArray(PaletteRows);
        json["TintEnabled"]    = TintEnabled;
        json["TintColors"]     = new JArray(TintColors);
        json["AlphaThreshold"] = AlphaThreshold;
        json["NormalSmooth"]    = NormalSmooth;
        json["Finish"]          = (int)Finish;
        json["FinishRoughness"] = FinishRoughness;
        json["FinishSpecScale"] = FinishSpecScale;
        json["EffectScale"]     = EffectScale;
        json["Surface"]        = Surface;
        json["AnchorX"]        = AnchorX;
        json["AnchorY"]        = AnchorY;
        json["AnchorZ"]        = AnchorZ;
        json["NormalX"]        = NormalX;
        json["NormalY"]        = NormalY;
        json["NormalZ"]        = NormalZ;
        json["WorldWidth"]         = WorldWidth;
        json["WorldHeight"]        = WorldHeight;
        json["SurfacePart"]        = SurfacePart;
        json["SurfaceLimitToPart"] = SurfaceLimitToPart;
        json["SurfaceAttributes"]  = SurfaceAttributes;
        json["SurfaceShapes"]      = SurfaceShapes;
        json["Extracted"]          = Extracted;
        json["SourceU"]            = SourceU;
        json["SourceV"]            = SourceV;
        json["SourceUW"]           = SourceUW;
        json["SourceUH"]           = SourceUH;
        json["FillPair"]           = FillPair;
        json["FillBlend"]          = FillBlend;
        json["WriteBlendFromAlpha"] = WriteBlendFromAlpha;
        if (PreExtractionSource != null)
            json["PreExtractionSource"] = PreExtractionSource;
    }

    public static DecalLayer LoadDecal(JObject json)
        => new()
        {
            DecalId        = json["DecalId"]?.ToObject<Guid>() ?? Guid.Empty,
            PosU           = json["PosU"]?.ToObject<float>() ?? 0.5f,
            PosV           = json["PosV"]?.ToObject<float>() ?? 0.5f,
            ScaleX         = json["ScaleX"]?.ToObject<float>() ?? 0.25f,
            ScaleY         = json["ScaleY"]?.ToObject<float>() ?? 0.25f,
            RotationDeg    = json["RotationDeg"]?.ToObject<float>() ?? 0f,
            FlipX          = json["FlipX"]?.ToObject<bool>() ?? false,
            FlipY          = json["FlipY"]?.ToObject<bool>() ?? false,
            Opacity        = json["Opacity"]?.ToObject<float>() ?? 1f,
            IdRemap        = json["IdRemap"]?.ToObject<bool>() ?? false,
            MaxColors      = json["MaxColors"]?.ToObject<int>() ?? 6,
            PaletteColors  = json["PaletteColors"]?.ToObject<List<uint>>() ?? [],
            PaletteRows    = json["PaletteRows"]?.ToObject<List<int>>() ?? [],
            TintEnabled    = json["TintEnabled"]?.ToObject<bool>() ?? false,
            TintColors     = json["TintColors"]?.ToObject<List<uint>>() ?? [],
            AlphaThreshold = json["AlphaThreshold"]?.ToObject<float>() ?? 0.5f,
            NormalSmooth   = json["NormalSmooth"]?.ToObject<float>() ?? 0f,
            // "Finish" replaced the pre-v0.5 "MaskPreset" key; old Matte/Glossy values map 1:1.
            Finish          = (DecalFinishMode)(json["Finish"]?.ToObject<int>() ?? json["MaskPreset"]?.ToObject<int>() ?? 0),
            FinishRoughness = json["FinishRoughness"]?.ToObject<float>() ?? 0.5f,
            FinishSpecScale = json["FinishSpecScale"]?.ToObject<float>() ?? 1f,
            EffectScale    = json["EffectScale"]?.ToObject<float>() ?? 1f,
            Surface        = json["Surface"]?.ToObject<bool>() ?? false,
            AnchorX        = json["AnchorX"]?.ToObject<float>() ?? 0f,
            AnchorY        = json["AnchorY"]?.ToObject<float>() ?? 0f,
            AnchorZ        = json["AnchorZ"]?.ToObject<float>() ?? 0f,
            NormalX        = json["NormalX"]?.ToObject<float>() ?? 0f,
            NormalY        = json["NormalY"]?.ToObject<float>() ?? 1f,
            NormalZ        = json["NormalZ"]?.ToObject<float>() ?? 0f,
            WorldWidth     = json["WorldWidth"]?.ToObject<float>() ?? 0.1f,
            WorldHeight    = json["WorldHeight"]?.ToObject<float>() ?? 0.1f,
            SurfacePart        = json["SurfacePart"]?.ToObject<int>() ?? -1,
            SurfaceLimitToPart = json["SurfaceLimitToPart"]?.ToObject<bool>() ?? true,
            SurfaceAttributes  = json["SurfaceAttributes"]?.ToObject<uint>() ?? uint.MaxValue,
            SurfaceShapes      = json["SurfaceShapes"]?.ToObject<uint>() ?? 0,
            Extracted          = json["Extracted"]?.ToObject<bool>() ?? false,
            SourceU            = json["SourceU"]?.ToObject<float>() ?? -1f,
            SourceV            = json["SourceV"]?.ToObject<float>() ?? 0f,
            SourceUW           = json["SourceUW"]?.ToObject<float>() ?? 0f,
            SourceUH           = json["SourceUH"]?.ToObject<float>() ?? 0f,
            FillPair           = json["FillPair"]?.ToObject<int>() ?? -1,
            FillBlend          = json["FillBlend"]?.ToObject<int>() ?? -1,
            WriteBlendFromAlpha = json["WriteBlendFromAlpha"]?.ToObject<bool>() ?? false,
            PreExtractionSource = json["PreExtractionSource"]?.ToObject<string>(),
        };
}
