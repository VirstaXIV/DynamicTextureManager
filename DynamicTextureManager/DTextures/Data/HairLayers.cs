using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.DTextures.Data;

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
