using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.DTextures.Data;

/// <summary>
/// Converts a hair material to the game's scrolling-effect shader (characterscroll.shpk) so
/// its highlight areas become an animated emissive effect. The replacement material reads a
/// colorset: pair 16's A row carries the effect (emissive color, driven by a scrolling
/// black/white effect texture), the B row the base hair color. An id map generated from the
/// hair normal's blue channel routes the authored+edited highlight distribution into that
/// pair, so every existing highlight edit still shapes WHERE the effect appears.
/// Configuration replicated from a community reference material (verified byte-identical
/// reconstruction offline).
/// </summary>
public sealed class AnimatedHairEdit
{
    public bool Enabled;

    /// <summary> Built-in effect pattern index (AnimatedHairBuilder.HairEffectPattern). </summary>
    public int Pattern;

    /// <summary> Absolute path of a custom pattern image — legacy/hidden; built-in patterns are the supported path. </summary>
    public string EffectImagePath = string.Empty;

    /// <summary> Scroll speed of the effect texture along U/V (reference default 1.5). </summary>
    public float SpeedU = 1.5f;

    public float SpeedV = 1.5f;

    /// <summary> Stretch of the effect texture along U/V (reference default 1). </summary>
    public float StretchU = 1f;

    public float StretchV = 1f;

    /// <summary> Emissive color of the moving effect (colorset row 16A), linear RGB. Always user-authored. </summary>
    public float[] EffectColor = [0.391f, 0.089f, 0.002f];

    /// <summary> Overall intensity multiplier applied to the effect color. </summary>
    public float EffectIntensity = 1f;

    /// <summary> Base hair color (colorset row 16B diffuse), linear RGB. Follows the character unless overridden. </summary>
    public float[] BaseColor = [0.1f, 0.1f, 0.1f];

    /// <summary> Hair color of the highlight areas under the glow (row 16A diffuse), linear RGB. Follows the character unless overridden. </summary>
    public float[] HighlightColor = [0.16f, 0.16f, 0.16f];

    /// <summary> When set, BaseColor/HighlightColor are used as stored instead of following the character's hair/highlight colors. </summary>
    public bool OverrideHairColors;

    public JObject Serialize()
        => new()
        {
            ["Enabled"]          = Enabled,
            ["Pattern"]          = Pattern,
            ["EffectImagePath"]  = EffectImagePath,
            ["SpeedU"]           = SpeedU,
            ["SpeedV"]           = SpeedV,
            ["StretchU"]         = StretchU,
            ["StretchV"]         = StretchV,
            ["EffectColor"]        = new JArray(EffectColor),
            ["EffectIntensity"]    = EffectIntensity,
            ["BaseColor"]          = new JArray(BaseColor),
            ["HighlightColor"]     = new JArray(HighlightColor),
            ["OverrideHairColors"] = OverrideHairColors,
        };

    public static AnimatedHairEdit Load(JObject json)
        => new()
        {
            Enabled          = json["Enabled"]?.ToObject<bool>() ?? false,
            Pattern          = json["Pattern"]?.ToObject<int>() ?? 0,
            EffectImagePath  = json["EffectImagePath"]?.ToObject<string>() ?? string.Empty,
            SpeedU           = json["SpeedU"]?.ToObject<float>() ?? 1.5f,
            SpeedV           = json["SpeedV"]?.ToObject<float>() ?? 1.5f,
            StretchU         = json["StretchU"]?.ToObject<float>() ?? 1f,
            StretchV         = json["StretchV"]?.ToObject<float>() ?? 1f,
            EffectColor      = LoadColor(json["EffectColor"], [0.391f, 0.089f, 0.002f]),
            EffectIntensity  = json["EffectIntensity"]?.ToObject<float>() ?? 1f,
            BaseColor        = LoadColor(json["BaseColor"], [0.1f, 0.1f, 0.1f]),
            HighlightColor   = LoadColor(json["HighlightColor"], [0.16f, 0.16f, 0.16f]),
            // Older saves: a manually pinned base color carries over as the override toggle.
            OverrideHairColors = json["OverrideHairColors"]?.ToObject<bool>()
             ?? json["BaseColorUserSet"]?.ToObject<bool>() ?? false,
        };

    private static float[] LoadColor(JToken? token, float[] fallback)
        => token is JArray { Count: 3 } array
            ? [array[0].ToObject<float>(), array[1].ToObject<float>(), array[2].ToObject<float>()]
            : fallback;

    public AnimatedHairEdit Clone()
        => Load(Serialize());
}
