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

    /// <summary> Runtime-only allocation failure shown in the UI; the layer auto-disables when set. </summary>
    public string? RowError;

    /// <summary> Decal pixels with alpha at or above this threshold are remapped in <see cref="IdRemap"/> mode. </summary>
    public float AlphaThreshold = 0.5f;

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
        json["Opacity"]        = Opacity;
        json["IdRemap"]        = IdRemap;
        json["MaxColors"]      = MaxColors;
        json["PaletteColors"]  = new JArray(PaletteColors);
        json["PaletteRows"]    = new JArray(PaletteRows);
        json["AlphaThreshold"] = AlphaThreshold;
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
            Opacity        = json["Opacity"]?.ToObject<float>() ?? 1f,
            IdRemap        = json["IdRemap"]?.ToObject<bool>() ?? false,
            MaxColors      = json["MaxColors"]?.ToObject<int>() ?? 6,
            PaletteColors  = json["PaletteColors"]?.ToObject<List<uint>>() ?? [],
            PaletteRows    = json["PaletteRows"]?.ToObject<List<int>>() ?? [],
            AlphaThreshold = json["AlphaThreshold"]?.ToObject<float>() ?? 0.5f,
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
        };
}
