using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.DTextures.Data;

public enum SourceType
{
    /// <summary> Materials picked from a character's resolved resource tree or an item. </summary>
    GamePath,

    /// <summary> An item picked from game data (resolved to game paths at build time). </summary>
    Item,

    /// <summary> Files of an installed Penumbra mod. </summary>
    PenumbraMod,
}

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
    /// the Textures tab like any other source, but excluded from the Decals tab's material
    /// selector — decorating it directly there merges most of the body mesh into unpaintable
    /// "context" (framed around the tiny overlay geometry) and renders it sampling the wrong
    /// texture at the wrong UVs, which is confusing, not useful. Its texture is instead painted
    /// automatically by a body-skin tattoo that overlaps it (OverlayModManager companion bake).
    /// </summary>
    public bool Overlay = false;

    public bool IsModded
        => ModDirectory.Length > 0;

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
        };
}

/// <summary> What a dTexture overlays. </summary>
public sealed class SourceRef
{
    public SourceType Type = SourceType.GamePath;

    /// <summary> Display name of the selection (character name, item name or mod name). </summary>
    public string DisplayName = string.Empty;

    /// <summary> The selected source materials. </summary>
    public List<SourcePath> Materials = [];

    /// <summary> Directory name of the source mod for <see cref="SourceType.PenumbraMod"/>. </summary>
    public string ModDirectory = string.Empty;

    /// <summary> Item id for <see cref="SourceType.Item"/>. </summary>
    public uint ItemId;

    public bool IsEmpty
        => Materials.Count == 0;

    public JObject Serialize()
        => new()
        {
            ["Type"]         = Type.ToString(),
            ["DisplayName"]  = DisplayName,
            ["Materials"]    = new JArray(Materials.Select(m => m.Serialize())),
            ["ModDirectory"] = ModDirectory,
            ["ItemId"]       = ItemId,
        };

    public static SourceRef Load(JObject? json)
    {
        if (json == null)
            return new SourceRef();

        var ret = new SourceRef
        {
            DisplayName  = json["DisplayName"]?.ToObject<string>() ?? string.Empty,
            ModDirectory = json["ModDirectory"]?.ToObject<string>() ?? string.Empty,
            ItemId       = json["ItemId"]?.ToObject<uint>() ?? 0,
        };
        if (Enum.TryParse<SourceType>(json["Type"]?.ToObject<string>(), out var type))
            ret.Type = type;
        if (json["Materials"] is JArray materials)
            ret.Materials = materials.OfType<JObject>().Select(SourcePath.Load).ToList();
        return ret;
    }

    public SourceRef Clone()
        => Load(Serialize());
}
