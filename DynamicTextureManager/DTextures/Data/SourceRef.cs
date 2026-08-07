using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.DTextures.Data;

/// <summary> A single source material: its game path plus the actual file it resolved to when selected. </summary>
public sealed class SourcePath
{
    /// <summary> The game path of the material, the stable key for overrides. </summary>
    public string GamePath = string.Empty;

    /// <summary> The file the game path resolved to at selection time (a modded file or the game path itself for vanilla). </summary>
    public string ActualPath = string.Empty;

    /// <summary> Display label captured from the resource tree (e.g. the equipment piece name). </summary>
    public string Label = string.Empty;

    /// <summary> Directory name of the Penumbra mod the actual file belonged to at selection time, empty for vanilla. </summary>
    public string ModDirectory = string.Empty;

    /// <summary> Display name of that mod, empty for vanilla. </summary>
    public string ModName = string.Empty;

    /// <summary> Game path of the model this material was found on, for UV layout display. </summary>
    public string MdlGamePath = string.Empty;

    /// <summary> The file that model resolved to at selection time. </summary>
    public string MdlActualPath = string.Empty;

    /// <summary>
    /// Whether this source is an overlay part (nails, accents — see ModelUvReader.
    /// GetBodyOverlayMaterials) rather than a primary editable canvas: shown and viewable in
    /// the Sources section like any other source, but excluded from the Decals tab's material
    /// selector — decorating it directly there merges most of the body mesh into unpaintable
    /// "context" (framed around the tiny overlay geometry) and renders it sampling the wrong
    /// texture at the wrong UVs, which is confusing, not useful. Its texture is instead painted
    /// automatically by a body-skin tattoo that overlaps it (OverlayModManager companion bake).
    /// </summary>
    public bool Overlay = false;

    /// <summary>
    /// Whether this overlay source is an alternate material set of the SAME body — the
    /// vanilla-compat material bibo-family body mods override so gear-embedded skin patches
    /// match (e.g. Muse's mt_c0201b0001_a on the vanilla texture paths). It is not its own
    /// canvas: every body bake (decals, procedural surface, relief) replays onto its texture
    /// set through the vanilla body layout, and the viewport never renders it — it would
    /// duplicate the body.
    /// </summary>
    public bool BodyMirror = false;

    public JObject Serialize()
        => new()
        {
            ["GamePath"]      = GamePath,
            ["ActualPath"]    = ActualPath,
            ["Label"]         = Label,
            ["ModDirectory"]  = ModDirectory,
            ["ModName"]       = ModName,
            ["MdlGamePath"]   = MdlGamePath,
            ["MdlActualPath"] = MdlActualPath,
            ["Overlay"]       = Overlay,
            ["BodyMirror"]    = BodyMirror,
        };

    public static SourcePath Load(JObject json)
        => new()
        {
            GamePath      = json["GamePath"]?.ToObject<string>() ?? string.Empty,
            ActualPath    = json["ActualPath"]?.ToObject<string>() ?? string.Empty,
            Label         = json["Label"]?.ToObject<string>() ?? string.Empty,
            ModDirectory  = json["ModDirectory"]?.ToObject<string>() ?? string.Empty,
            ModName       = json["ModName"]?.ToObject<string>() ?? string.Empty,
            MdlGamePath   = json["MdlGamePath"]?.ToObject<string>() ?? string.Empty,
            MdlActualPath = json["MdlActualPath"]?.ToObject<string>() ?? string.Empty,
            Overlay       = json["Overlay"]?.ToObject<bool>() ?? false,
            BodyMirror    = json["BodyMirror"]?.ToObject<bool>() ?? false,
        };
}

/// <summary> What a dTexture overlays. </summary>
public sealed class SourceRef
{
    /// <summary> The selected source materials. </summary>
    public List<SourcePath> Materials = [];

    public bool IsEmpty
        => Materials.Count == 0;

    public JObject Serialize()
        => new()
        {
            ["Materials"] = new JArray(Materials.Select(m => m.Serialize())),
        };

    public static SourceRef Load(JObject? json)
    {
        if (json == null)
            return new SourceRef();

        var ret = new SourceRef();
        if (json["Materials"] is JArray materials)
            ret.Materials = materials.OfType<JObject>().Select(SourcePath.Load).ToList();
        return ret;
    }

    public SourceRef Clone()
        => Load(Serialize());
}
