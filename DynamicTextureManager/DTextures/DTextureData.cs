using System.Collections.Generic;
using System.Linq;
using DynamicTextureManager.DTextures.Data;
using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.DTextures;

/// <summary> The payload of a dTexture: what it overlays and every edit applied on top. </summary>
public sealed class DTextureData
{
    /// <summary> What is being overlaid. </summary>
    public SourceRef Source = new();

    /// <summary> Colorset edits keyed by material game path. </summary>
    public Dictionary<string, MaterialEdit> Materials = [];

    /// <summary> Texture layer stacks keyed by texture game path. </summary>
    public Dictionary<string, List<TextureLayer>> Textures = [];

    /// <summary>
    /// The original (possibly modded) file each layered texture resolved to when its first
    /// layer was added. Builds always re-bake from these pristine sources — never from a
    /// build-time resolve, which would return our own generated file and compound the bake.
    /// </summary>
    public Dictionary<string, string> TextureSourcePaths = [];

    /// <summary> Directory name of the generated Penumbra mod, empty while never built. </summary>
    public string OutputModDirectory = string.Empty;

    /// <summary> Hash over the source inputs of the last successful build, for change detection. </summary>
    public string LastBuiltHash = string.Empty;

    public bool HasEdits
        => Materials.Values.Any(m => !m.IsEmpty) || Textures.Values.Any(t => t.Count > 0);

    public JObject Serialize()
        => new()
        {
            ["Source"] = Source.Serialize(),
            ["Materials"] = new JObject(Materials
                .Where(kvp => !kvp.Value.IsEmpty)
                .Select(kvp => new JProperty(kvp.Key, kvp.Value.Serialize()))),
            ["Textures"] = new JObject(Textures
                .Where(kvp => kvp.Value.Count > 0)
                .Select(kvp => new JProperty(kvp.Key, new JArray(kvp.Value.Select(l => l.Serialize()))))),
            ["TextureSourcePaths"] = new JObject(TextureSourcePaths
                .Select(kvp => new JProperty(kvp.Key, kvp.Value))),
            ["OutputModDirectory"] = OutputModDirectory,
            ["LastBuiltHash"]      = LastBuiltHash,
        };

    public static DTextureData Load(JObject? json)
    {
        if (json == null)
            return new DTextureData();

        var ret = new DTextureData
        {
            Source             = SourceRef.Load(json["Source"] as JObject),
            OutputModDirectory = json["OutputModDirectory"]?.ToObject<string>() ?? string.Empty,
            LastBuiltHash      = json["LastBuiltHash"]?.ToObject<string>() ?? string.Empty,
        };

        if (json["Materials"] is JObject materials)
            foreach (var property in materials.Properties())
                if (property.Value is JObject value)
                    ret.Materials[property.Name] = MaterialEdit.Load(value);

        if (json["Textures"] is JObject textures)
            foreach (var property in textures.Properties())
                ret.Textures[property.Name] = TextureLayer.LoadList(property.Value);

        if (json["TextureSourcePaths"] is JObject sourcePaths)
            foreach (var property in sourcePaths.Properties())
                ret.TextureSourcePaths[property.Name] = property.Value.ToObject<string>() ?? string.Empty;

        return ret;
    }

    public DTextureData Clone()
        => Load(Serialize());
}
