using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.DTextures.Data;

/// <summary> Where the highlights fundamentally sit before the strand pattern and shaping apply. </summary>
public enum HighlightBase
{
    /// <summary> The hairstyle's authored highlight layout, untouched. </summary>
    Layout = 0,

    /// <summary> The authored layout reversed — highlighted areas become plain and vice versa. </summary>
    Inverted = 1,

    /// <summary> A zone growing from the top of the texture (usually the roots), its reach set by the extent dial. </summary>
    Roots = 2,

    /// <summary> A zone growing from the bottom of the texture (usually the tips), its reach set by the extent dial. </summary>
    Tips = 3,

    /// <summary> No highlights anywhere. </summary>
    MainOnly = 4,

    /// <summary> Highlight color everywhere (at the style's own highlight intensity). </summary>
    HighlightOnly = 5,

    /// <summary> Random strands lit ON TOP of the authored layout. </summary>
    StrandsAdd = 6,

    /// <summary> Random strands REPLACE the authored layout. </summary>
    StrandsOnly = 7,
}

/// <summary> How the generated strand pattern combines with the highlight base. </summary>
public enum HighlightBlendMode
{
    /// <summary> Carve the pattern OUT of the authored layout — masked strands lose their highlights. </summary>
    Multiply = 0,

    /// <summary> Add the pattern's strands on top of the authored layout — works on styles authored with none. </summary>
    Screen = 1,

    /// <summary> The pattern relocates the highlights: authored streaks fade out, pattern strands fade in. </summary>
    Replace = 2,
}

/// <summary>
/// Global adjustment of a hair material's highlight distribution, applied to the hair NORMAL
/// texture's blue channel (the per-pixel blend between the wearer's main hair color and
/// highlight color; R/G tangent normals and A opacity are never touched). The strand noise
/// generates streaks that follow the texture's strand direction and relights a random subset
/// of them (coverage/seed), a directional gradient fades the result across the style, and
/// contrast/bias reshape the blend globally. All neutral defaults compose to an exact no-op.
/// </summary>
public sealed class HairHighlightLayer : TextureLayer
{
    public const string Type = "HairHighlight";

    /// <summary> The fundamental highlight placement the pattern and shaping start from. </summary>
    public HighlightBase Base = HighlightBase.Layout;

    /// <summary> For the Roots/Tips bases: how far along the texture the highlight zone reaches. </summary>
    public float BaseExtent = 0.4f;

    /// <summary> Feathering of the Roots/Tips zone boundary. </summary>
    public float BaseFeather = 0.25f;

    /// <summary>
    /// Per-strand naturalization of everything generated: strand-by-strand intensity jitter
    /// plus ragged along-strand breakup, so generated highlights read as layered hair rather
    /// than flat paint. On the plain layout it varies the authored highlights the same way.
    /// </summary>
    public float StrandVariation = 0.35f;

    public HighlightBlendMode Mode = HighlightBlendMode.Screen;

    /// <summary> Final mix between the untouched channel and the adjusted result. </summary>
    public float Strength = 1f;

    public bool  NoiseEnabled;
    public int   Seed          = 1337;
    public float NoiseScale    = 48f;
    public int   Octaves       = 3;
    public float NoiseStrength = 1f;

    /// <summary> How strongly the noise streaks along the strand direction (1 = round blobs). </summary>
    public float Elongation = 6f;

    /// <summary> Fraction of the pattern that lights up — how many strands carry the highlight. </summary>
    public float Coverage = 0.5f;

    /// <summary> Feathering of the streak edges (0 = hard-cut strands, higher = soft fades). </summary>
    public float Softness = 0.15f;

    public bool  GradientEnabled;
    public float GradientAngleDeg = 90f;
    public float GradientStart;
    public float GradientEnd = 1f;
    public bool  GradientInvert;
    public float GradientStrength = 1f;

    /// <summary> Contrast around 0.5 applied to the combined mask (1 = unchanged). </summary>
    public float Contrast = 1f;

    /// <summary> Flat offset applied to the combined mask (0 = unchanged). </summary>
    public float Bias;

    /// <summary> Whether every parameter is at its neutral value — the composite is an exact no-op. </summary>
    public bool IsNeutral
        => Strength <= 0f
        || Base is HighlightBase.Layout && StrandVariation == 0f && !GradientEnabled && Contrast == 1f && Bias == 0f;

    public override string LayerType
        => Type;

    protected override void SerializeInto(JObject json)
    {
        json["Base"]             = (int)Base;
        json["BaseExtent"]       = BaseExtent;
        json["BaseFeather"]      = BaseFeather;
        json["StrandVariation"]  = StrandVariation;
        json["Mode"]             = (int)Mode;
        json["Strength"]         = Strength;
        json["NoiseEnabled"]     = NoiseEnabled;
        json["Seed"]             = Seed;
        json["NoiseScale"]       = NoiseScale;
        json["Octaves"]          = Octaves;
        json["NoiseStrength"]    = NoiseStrength;
        json["Elongation"]       = Elongation;
        json["Coverage"]         = Coverage;
        json["Softness"]         = Softness;
        json["GradientEnabled"]  = GradientEnabled;
        json["GradientAngleDeg"] = GradientAngleDeg;
        json["GradientStart"]    = GradientStart;
        json["GradientEnd"]      = GradientEnd;
        json["GradientInvert"]   = GradientInvert;
        json["GradientStrength"] = GradientStrength;
        json["Contrast"]         = Contrast;
        json["Bias"]             = Bias;
    }

    public static HairHighlightLayer LoadHighlight(JObject json)
    {
        var ret = new HairHighlightLayer
        {
            Base             = (HighlightBase)(json["Base"]?.ToObject<int>() ?? 0),
            BaseExtent       = json["BaseExtent"]?.ToObject<float>() ?? 0.4f,
            BaseFeather      = json["BaseFeather"]?.ToObject<float>() ?? 0.25f,
            StrandVariation  = json["StrandVariation"]?.ToObject<float>() ?? 0.35f,
            Mode             = (HighlightBlendMode)(json["Mode"]?.ToObject<int>() ?? (int)HighlightBlendMode.Screen),
            Strength         = json["Strength"]?.ToObject<float>() ?? 1f,
            NoiseEnabled     = json["NoiseEnabled"]?.ToObject<bool>() ?? false,
            Seed             = json["Seed"]?.ToObject<int>() ?? 1337,
            NoiseScale       = json["NoiseScale"]?.ToObject<float>() ?? 48f,
            Octaves          = json["Octaves"]?.ToObject<int>() ?? 3,
            NoiseStrength    = json["NoiseStrength"]?.ToObject<float>() ?? 1f,
            Elongation       = json["Elongation"]?.ToObject<float>() ?? 6f,
            Coverage         = json["Coverage"]?.ToObject<float>() ?? 0.5f,
            Softness         = json["Softness"]?.ToObject<float>() ?? 0.15f,
            GradientEnabled  = json["GradientEnabled"]?.ToObject<bool>() ?? false,
            GradientAngleDeg = json["GradientAngleDeg"]?.ToObject<float>() ?? 90f,
            GradientStart    = json["GradientStart"]?.ToObject<float>() ?? 0f,
            GradientEnd      = json["GradientEnd"]?.ToObject<float>() ?? 1f,
            GradientInvert   = json["GradientInvert"]?.ToObject<bool>() ?? false,
            GradientStrength = json["GradientStrength"]?.ToObject<float>() ?? 1f,
            Contrast         = json["Contrast"]?.ToObject<float>() ?? 1f,
            Bias             = json["Bias"]?.ToObject<float>() ?? 0f,
        };

        // Migration from the pre-placement strand-noise overlay: an enabled noise on the plain
        // layout becomes the equivalent strands placement; the standalone overlay is gone.
        if (ret is { NoiseEnabled: true, Base: HighlightBase.Layout })
            ret.Base = ret.Mode is HighlightBlendMode.Replace ? HighlightBase.StrandsOnly : HighlightBase.StrandsAdd;

        return ret;
    }
}

/// <summary>
/// Global surface controls for a hair material, applied to the hair MASK texture's channels:
/// R specular power, G roughness, B subsurface thickness, A ambient occlusion. Simple per-
/// channel scales (plus a roughness offset), enough for glossy/matte adjustments without
/// pretending more precision than the empirical channel semantics support.
/// </summary>
public sealed class HairShineLayer : TextureLayer
{
    public const string Type = "HairShine";

    public float SpecScale       = 1f;
    public float RoughnessScale  = 1f;
    public float RoughnessOffset;
    public float SssScale        = 1f;
    public float AoScale         = 1f;

    /// <summary> Whether every parameter is at its neutral value — the composite is an exact no-op. </summary>
    public bool IsNeutral
        => SpecScale == 1f && RoughnessScale == 1f && RoughnessOffset == 0f && SssScale == 1f && AoScale == 1f;

    public override string LayerType
        => Type;

    protected override void SerializeInto(JObject json)
    {
        json["SpecScale"]       = SpecScale;
        json["RoughnessScale"]  = RoughnessScale;
        json["RoughnessOffset"] = RoughnessOffset;
        json["SssScale"]        = SssScale;
        json["AoScale"]         = AoScale;
    }

    public static HairShineLayer LoadShine(JObject json)
        => new()
        {
            SpecScale       = json["SpecScale"]?.ToObject<float>() ?? 1f,
            RoughnessScale  = json["RoughnessScale"]?.ToObject<float>() ?? 1f,
            RoughnessOffset = json["RoughnessOffset"]?.ToObject<float>() ?? 0f,
            SssScale        = json["SssScale"]?.ToObject<float>() ?? 1f,
            AoScale         = json["AoScale"]?.ToObject<float>() ?? 1f,
        };
}
