using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.DTextures.Data;

/// <summary>
/// Converts a modern colorset gear material to the game's scrolling-effect shader
/// (characterscroll.shpk) so SELECTED colorset rows gain an animated emissive glow.
/// Replicated from vanilla gear that ships this way (e6257): the material stays ordinary
/// gear data — its own textures, id map, rows and dye table — plus one added catchlight
/// (pattern) texture and, on the chosen rows, an emissive color and the effect field that
/// makes the pattern scroll across them.
/// </summary>
public sealed class AnimatedGearEdit
{
    public bool Enabled;

    /// <summary> Colorset row indices (0-31) that carry the glow. </summary>
    public List<int> Rows = [];

    /// <summary> Built-in effect pattern index (AnimatedHairBuilder.HairEffectPattern). </summary>
    public int Pattern;

    /// <summary> Scroll speed of the effect texture along U/V in UV units per second. </summary>
    public float ScrollU;

    public float ScrollV = 0.15f;

    /// <summary> Pattern tiling along U/V — how often the effect texture repeats. </summary>
    public float TilingU = 1f;

    public float TilingV = 1f;

    /// <summary> Emissive color of the glow, colorset (squared) domain like the hair edit's. </summary>
    public float[] EffectColor = [0.391f, 0.089f, 0.002f];

    /// <summary>
    /// Overall intensity multiplier applied to the effect color. Defaults HIGH: vanilla
    /// glowing gear (Neo Queen's Dress, e6257) authors HDR emissive around 1.2–1.7 because
    /// the shader squares the pattern sample before multiplying — at intensity 1 the glow
    /// measures ~45× dimmer than the vanilla reference and is invisible on lit gear.
    /// </summary>
    public float EffectIntensity = 4f;

    public JObject Serialize()
        => new()
        {
            ["Enabled"]         = Enabled,
            ["Rows"]            = new JArray(Rows),
            ["Pattern"]         = Pattern,
            ["ScrollU"]         = ScrollU,
            ["ScrollV"]         = ScrollV,
            ["TilingU"]         = TilingU,
            ["TilingV"]         = TilingV,
            ["EffectColor"]     = new JArray(EffectColor),
            ["EffectIntensity"] = EffectIntensity,
        };

    public static AnimatedGearEdit Load(JObject json)
        => new()
        {
            Enabled         = json["Enabled"]?.ToObject<bool>() ?? false,
            Rows            = json["Rows"]?.ToObject<List<int>>()?.Where(r => r is >= 0 and < 32).Distinct().ToList() ?? [],
            Pattern         = json["Pattern"]?.ToObject<int>() ?? 0,
            ScrollU         = json["ScrollU"]?.ToObject<float>() ?? 0f,
            ScrollV         = json["ScrollV"]?.ToObject<float>() ?? 0.15f,
            TilingU         = json["TilingU"]?.ToObject<float>() ?? 1f,
            TilingV         = json["TilingV"]?.ToObject<float>() ?? 1f,
            EffectColor     = json["EffectColor"] is JArray { Count: 3 } color
                ? [color[0].ToObject<float>(), color[1].ToObject<float>(), color[2].ToObject<float>()]
                : [0.391f, 0.089f, 0.002f],
            EffectIntensity = json["EffectIntensity"]?.ToObject<float>() ?? 4f,
        };

    public AnimatedGearEdit Clone()
        => Load(Serialize());
}
