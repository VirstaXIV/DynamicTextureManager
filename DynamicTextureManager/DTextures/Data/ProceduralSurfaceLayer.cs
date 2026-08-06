using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.DTextures.Data;

/// <summary> Which procedural generator a surface layer runs. </summary>
public enum SurfaceGeneratorKind
{
    Pattern = 0,
    Scales  = 1,
    Fur     = 2,
}

/// <summary> Pattern generator styles. </summary>
public enum SurfacePatternStyle
{
    Spots    = 0,
    Stripes  = 1,
    Marbling = 2,
}

/// <summary>
/// Markings: where the highlight color interacts with the base color — tabby banding,
/// spots, marbled swirls, or a hand-painted mask. Available on every generator kind.
/// None leaves a plain single-color surface.
/// </summary>
public enum FurMarkingStyle
{
    None     = 0,
    Stripes  = 1,
    Spots    = 2,
    Marbling = 3,
    Painted  = 4,
    Custom   = 5,
}

/// <summary>
/// A guide anchor steering the surface flow field: a point on the mesh with a direction the
/// pattern should follow there. Flow is geodesically propagated from every anchor and blended
/// by inverse geodesic distance. Exclusion anchors instead fade the pattern out around them.
/// </summary>
public sealed class FlowAnchor
{
    /// <summary> Anchor position on the mesh, bind-pose model space. </summary>
    public float PosX, PosY, PosZ;

    /// <summary> Surface normal at the anchor, bind-pose model space. </summary>
    public float NormalX, NormalY = 1f, NormalZ;

    /// <summary> Flow direction at the anchor, bind-pose model space (projected into the tangent plane). </summary>
    public float DirX, DirY = -1f, DirZ;

    /// <summary> Blend weight multiplier against other anchors. </summary>
    public float Strength = 1f;

    /// <summary> Fade the pattern out around this anchor instead of steering the flow. </summary>
    public bool Exclude;

    /// <summary> Radius of the excluded area in meters, measured along the surface. </summary>
    public float Radius = 0.08f;

    /// <summary> Width of the fade band beyond <see cref="Radius"/>, meters. </summary>
    public float Feather = 0.04f;

    public JObject Serialize()
        => new()
        {
            ["PosX"]     = PosX,
            ["PosY"]     = PosY,
            ["PosZ"]     = PosZ,
            ["NormalX"]  = NormalX,
            ["NormalY"]  = NormalY,
            ["NormalZ"]  = NormalZ,
            ["DirX"]     = DirX,
            ["DirY"]     = DirY,
            ["DirZ"]     = DirZ,
            ["Strength"] = Strength,
            ["Exclude"]  = Exclude,
            ["Radius"]   = Radius,
            ["Feather"]  = Feather,
        };

    public static FlowAnchor Load(JObject json)
        => new()
        {
            PosX     = json["PosX"]?.ToObject<float>() ?? 0f,
            PosY     = json["PosY"]?.ToObject<float>() ?? 0f,
            PosZ     = json["PosZ"]?.ToObject<float>() ?? 0f,
            NormalX  = json["NormalX"]?.ToObject<float>() ?? 0f,
            NormalY  = json["NormalY"]?.ToObject<float>() ?? 1f,
            NormalZ  = json["NormalZ"]?.ToObject<float>() ?? 0f,
            DirX     = json["DirX"]?.ToObject<float>() ?? 0f,
            DirY     = json["DirY"]?.ToObject<float>() ?? -1f,
            DirZ     = json["DirZ"]?.ToObject<float>() ?? 0f,
            Strength = json["Strength"]?.ToObject<float>() ?? 1f,
            Exclude  = json["Exclude"]?.ToObject<bool>() ?? false,
            Radius   = json["Radius"]?.ToObject<float>() ?? 0.08f,
            Feather  = json["Feather"]?.ToObject<float>() ?? 0.04f,
        };
}

/// <summary>
/// One brush dab of the coverage paint: a sphere in bind-pose model space that fades the
/// pattern out (or back in, for restore dabs) with a soft falloff band around its radius.
/// Strokes serialize as their dabs, so the painted mask rebuilds on any mesh.
/// </summary>
public sealed class CoverageDab
{
    public float X, Y, Z;
    public float Radius   = 0.05f;
    public float Strength = 1f;
    public bool  Restore;

    public JArray Serialize()
        => [X, Y, Z, Radius, Strength, Restore ? 1 : 0];

    public static CoverageDab? Load(JArray json)
        => json.Count < 6
            ? null
            : new CoverageDab
            {
                X        = json[0].ToObject<float>(),
                Y        = json[1].ToObject<float>(),
                Z        = json[2].ToObject<float>(),
                Radius   = json[3].ToObject<float>(),
                Strength = json[4].ToObject<float>(),
                Restore  = json[5].ToObject<int>() != 0,
            };
}

/// <summary>
/// A procedural full-surface layer: fur, scales or a skin pattern generated over the whole
/// editable mesh surface, oriented by a guide-anchor flow field. Evaluated in world space on
/// the mesh, so the result is seamless across UV islands and material splits. Also drives
/// the material's normal (relief) and mask (finish) textures through the sibling machinery.
/// </summary>
public sealed class ProceduralSurfaceLayer : TextureLayer
{
    public const string Type = "ProceduralSurface";

    public SurfaceGeneratorKind Kind;

    public int Seed = 1;

    public List<FlowAnchor> Anchors = [];

    /// <summary> Legacy shared size — kept for older saves; the per-kind sizes below rule. </summary>
    public float FeatureSizeCm = 2f;

    /// <summary>
    /// Feature size per kind, in centimeters. Separate on purpose: fur only reads as fur
    /// at small strand sizes while scales and patterns live at centimeter scale — one
    /// shared slider kept dragging a fur-tuned value into the other kinds.
    /// </summary>
    public float FurSizeCm = 0.3f;

    public float ScaleSizeCm = 2f;

    public float PatternSizeCm = 2f;

    /// <summary> The active kind's feature size. </summary>
    public float ActiveSizeCm
    {
        get => Kind switch
        {
            SurfaceGeneratorKind.Fur    => FurSizeCm,
            SurfaceGeneratorKind.Scales => ScaleSizeCm,
            _                           => PatternSizeCm,
        };
        set
        {
            switch (Kind)
            {
                case SurfaceGeneratorKind.Fur:    FurSizeCm = value; break;
                case SurfaceGeneratorKind.Scales: ScaleSizeCm = value; break;
                default:                          PatternSizeCm = value; break;
            }
        }
    }

    /// <summary> Primary color, packed Rgba32 (0xAABBGGRR). </summary>
    public uint ColorA = 0xFF303030;

    /// <summary> Secondary color, packed Rgba32 (0xAABBGGRR). </summary>
    public uint ColorB = 0xFF406080;

    /// <summary>
    /// Color like the character: hair main color as the base, the highlight color on the
    /// crests — read live at bake time (Glamourer included), like the animated hair colors.
    /// </summary>
    public bool UseCharacterColors = true;

    /// <summary> Shift both colors toward the character's current skin tone at bake time. </summary>
    public bool TintFromSkin;

    public float Opacity = 1f;

    /// <summary> Height/coverage contrast remap around the midpoint. </summary>
    public float Contrast = 0.5f;

    /// <summary> Low-frequency color jitter across the surface (and per scale cell). </summary>
    public float ColorVariation = 0.25f;

    /// <summary> Strength of the relief written into the normal map; 0 disables the normal write. </summary>
    public float HeightStrength = 0.5f;

    /// <summary> Roughness shift written into the mask map (-1 glossier .. +1 more matte); 0 disables. </summary>
    public float RoughnessAmount;

    /// <summary> Crevice darkening baked into the diffuse (and mask cavity when enabled). </summary>
    public float CavityAmount = 0.3f;

    // Pattern
    public SurfacePatternStyle PatternStyle;
    public float WarpStrength = 0.4f;
    public float Threshold    = 0.5f;

    // Scales
    public float ScaleElongation = 1.5f;
    public float BevelWidth      = 0.3f;

    // Fur
    public float StrandAspect = 8f;
    public float Curl         = 0.3f;
    public float SpeckDensity = 0.5f;

    /// <summary> Coat markings rendered in the highlight color over the main coat. </summary>
    public FurMarkingStyle Markings;

    public float MarkingScaleCm = 7f;

    /// <summary> How much of the coat the markings cover. </summary>
    public float MarkingAmount = 0.5f;

    /// <summary> The library pattern sampled as the markings mask when <see cref="Markings"/> is Custom. </summary>
    public Guid MarkingPatternId;

    /// <summary> Per-body-part weights, indexed by MaterialMesh.TriangleUnit (top, legs, hands, feet). </summary>
    public float WeightChest = 1f, WeightLegs = 1f, WeightHands = 1f, WeightFeet = 1f;

    /// <summary> Coverage on the face companion canvas (its own mesh, not a body unit). </summary>
    public float WeightFace = 1f;

    /// <summary> The painted coverage mask, as its brush dabs in stroke order. </summary>
    public List<CoverageDab> MaskDabs = [];

    /// <summary> The painted markings mask (highlight-color placement), same dab model. </summary>
    public List<CoverageDab> MarkingDabs = [];

    /// <summary> Softening of part-boundary transitions (0 = hard cut at the unit seam). </summary>
    public float RegionFeather = 0.5f;

    /// <summary> Attribute mask of visible submeshes captured when the layer was added. </summary>
    public uint SurfaceAttributes = uint.MaxValue;

    public override string LayerType
        => Type;

    public override bool NeedsMeshGeometry
        => true;

    public override bool HasSiblingEffects
        => WantsNormalEffect || WantsMaskEffect;

    public override bool WantsNormalEffect
        => HeightStrength > 0f;

    public override bool WantsMaskEffect
        => RoughnessAmount != 0f
         || (CavityAmount > 0f && ModGeneration.FinishMapping.ProceduralMaskWriteCavity);

    public float UnitWeight(int unit)
        => unit switch
        {
            1 => WeightLegs,
            2 => WeightHands,
            3 => WeightFeet,
            _ => WeightChest,
        };

    /// <summary>
    /// Content key for the bake cache: any serialized state change (and only that) must
    /// produce a new key, so it is derived from the persisted form itself.
    /// </summary>
    public string ContentHash()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Serialize().ToString(Newtonsoft.Json.Formatting.None));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    protected override void SerializeInto(JObject json)
    {
        json["Kind"]            = (int)Kind;
        json["Seed"]            = Seed;
        json["Anchors"]         = new JArray(Anchors.Select(a => a.Serialize()));
        json["FeatureSizeCm"]   = FeatureSizeCm;
        json["FurSizeCm"]       = FurSizeCm;
        json["ScaleSizeCm"]     = ScaleSizeCm;
        json["PatternSizeCm"]   = PatternSizeCm;
        json["ColorA"]          = ColorA;
        json["ColorB"]          = ColorB;
        json["UseCharacterColors"] = UseCharacterColors;
        json["TintFromSkin"]    = TintFromSkin;
        json["Opacity"]         = Opacity;
        json["Contrast"]        = Contrast;
        json["ColorVariation"]  = ColorVariation;
        json["HeightStrength"]  = HeightStrength;
        json["RoughnessAmount"] = RoughnessAmount;
        json["CavityAmount"]    = CavityAmount;
        json["PatternStyle"]    = (int)PatternStyle;
        json["WarpStrength"]    = WarpStrength;
        json["Threshold"]       = Threshold;
        json["ScaleElongation"] = ScaleElongation;
        json["BevelWidth"]      = BevelWidth;
        json["StrandAspect"]    = StrandAspect;
        json["Curl"]            = Curl;
        json["SpeckDensity"]    = SpeckDensity;
        json["Markings"]        = (int)Markings;
        json["MarkingScaleCm"]  = MarkingScaleCm;
        json["MarkingAmount"]   = MarkingAmount;
        json["MarkingPatternId"] = MarkingPatternId;
        json["WeightChest"]     = WeightChest;
        json["WeightLegs"]      = WeightLegs;
        json["WeightHands"]     = WeightHands;
        json["WeightFeet"]      = WeightFeet;
        json["WeightFace"]      = WeightFace;
        json["RegionFeather"]   = RegionFeather;
        json["MaskDabs"]        = new JArray(MaskDabs.Select(d => d.Serialize()));
        json["MarkingDabs"]     = new JArray(MarkingDabs.Select(d => d.Serialize()));
        json["SurfaceAttributes"] = SurfaceAttributes;
    }

    public static ProceduralSurfaceLayer LoadProcedural(JObject json)
    {
        var ret = new ProceduralSurfaceLayer
        {
            Kind            = (SurfaceGeneratorKind)(json["Kind"]?.ToObject<int>() ?? 0),
            Seed            = json["Seed"]?.ToObject<int>() ?? 1,
            Anchors         = json["Anchors"] is JArray anchors
                ? anchors.OfType<JObject>().Select(FlowAnchor.Load).ToList()
                : [],
            FeatureSizeCm   = json["FeatureSizeCm"]?.ToObject<float>() ?? 2f,
            ColorA          = json["ColorA"]?.ToObject<uint>() ?? 0xFF303030,
            ColorB          = json["ColorB"]?.ToObject<uint>() ?? 0xFF406080,
            UseCharacterColors = json["UseCharacterColors"]?.ToObject<bool>() ?? true,
            TintFromSkin    = json["TintFromSkin"]?.ToObject<bool>() ?? false,
            Opacity         = json["Opacity"]?.ToObject<float>() ?? 1f,
            Contrast        = json["Contrast"]?.ToObject<float>() ?? 0.5f,
            ColorVariation  = json["ColorVariation"]?.ToObject<float>() ?? 0.25f,
            HeightStrength  = json["HeightStrength"]?.ToObject<float>() ?? 0.5f,
            RoughnessAmount = json["RoughnessAmount"]?.ToObject<float>() ?? 0f,
            CavityAmount    = json["CavityAmount"]?.ToObject<float>() ?? 0.3f,
            PatternStyle    = (SurfacePatternStyle)(json["PatternStyle"]?.ToObject<int>() ?? 0),
            WarpStrength    = json["WarpStrength"]?.ToObject<float>() ?? 0.4f,
            Threshold       = json["Threshold"]?.ToObject<float>() ?? 0.5f,
            ScaleElongation = json["ScaleElongation"]?.ToObject<float>() ?? 1.5f,
            BevelWidth      = json["BevelWidth"]?.ToObject<float>() ?? 0.3f,
            StrandAspect    = json["StrandAspect"]?.ToObject<float>() ?? 8f,
            Curl            = json["Curl"]?.ToObject<float>() ?? 0.3f,
            SpeckDensity    = json["SpeckDensity"]?.ToObject<float>() ?? 0.5f,
            Markings        = (FurMarkingStyle)(json["Markings"]?.ToObject<int>() ?? 0),
            MarkingScaleCm  = json["MarkingScaleCm"]?.ToObject<float>() ?? 7f,
            MarkingAmount   = json["MarkingAmount"]?.ToObject<float>() ?? 0.5f,
            MarkingPatternId = json["MarkingPatternId"]?.ToObject<Guid>() ?? Guid.Empty,
            WeightChest     = json["WeightChest"]?.ToObject<float>() ?? 1f,
            WeightLegs      = json["WeightLegs"]?.ToObject<float>() ?? 1f,
            WeightHands     = json["WeightHands"]?.ToObject<float>() ?? 1f,
            WeightFeet      = json["WeightFeet"]?.ToObject<float>() ?? 1f,
            WeightFace      = json["WeightFace"]?.ToObject<float>() ?? 1f,
            RegionFeather   = json["RegionFeather"]?.ToObject<float>() ?? 0.5f,
            MaskDabs        = json["MaskDabs"] is JArray dabs
                ? dabs.OfType<JArray>().Select(CoverageDab.Load).OfType<CoverageDab>().ToList()
                : [],
            MarkingDabs     = json["MarkingDabs"] is JArray marks
                ? marks.OfType<JArray>().Select(CoverageDab.Load).OfType<CoverageDab>().ToList()
                : [],
            SurfaceAttributes = json["SurfaceAttributes"]?.ToObject<uint>() ?? uint.MaxValue,
        };

        // Older saves carried one shared size: it becomes the ACTIVE kind's size; the other
        // kinds start from their own defaults instead of inheriting a mistuned value.
        ret.FurSizeCm     = json["FurSizeCm"]?.ToObject<float>() ?? (ret.Kind == SurfaceGeneratorKind.Fur ? ret.FeatureSizeCm : 0.3f);
        ret.ScaleSizeCm   = json["ScaleSizeCm"]?.ToObject<float>() ?? (ret.Kind == SurfaceGeneratorKind.Scales ? ret.FeatureSizeCm : 2f);
        ret.PatternSizeCm = json["PatternSizeCm"]?.ToObject<float>() ?? (ret.Kind == SurfaceGeneratorKind.Pattern ? ret.FeatureSizeCm : 2f);
        return ret;
    }
}
