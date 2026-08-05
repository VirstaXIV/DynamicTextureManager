using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Configuration;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.Services;
using DynamicTextureManager.UI;
using Dalamud.Game.ClientState.Keys;
using Luna;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ErrorEventArgs = Newtonsoft.Json.Serialization.ErrorEventArgs;

namespace DynamicTextureManager;

public class Configuration: IPluginConfiguration, ISavable
{
    public bool AutoReload { get; set; } = true;
    public int OverlayPriority { get; set; } = 999;
    public bool DeleteModWithDTexture { get; set; } = true;
    public int DefaultDecalMaxColors { get; set; } = 6;
    [JsonConverter(typeof(DoubleModifierCompatConverter))]
    public DoubleModifier DeleteDTextureModifier { get; set; } = new(ModifierHotkey.Control, ModifierHotkey.Shift);

    /// <summary> Folder decal images are stored in; empty uses the default inside the plugin config directory. </summary>
    public string DecalStorageFolder { get; set; } = string.Empty;

    /// <summary>
    /// Preview-only skin tone (packed Rgba32) multiplied onto skin materials in the 3D
    /// viewport. Skin diffuse textures are pale neutral maps the game tints with the
    /// character's customize skin color in-shader; without a stand-in tone the preview
    /// looks nothing like in-game skin. Never written into any texture.
    /// </summary>
    public uint PreviewSkinTone { get; set; } = 0xFF8AAAD6;


    /// <summary>
    /// Preview-only hair colors (packed Rgba32) for hair materials in the 3D viewport. Hair has
    /// no diffuse texture — the game lerps the customize main/highlight colors by the normal
    /// map's blue channel in-shader, so the preview needs stand-in colors. Never written into
    /// any texture.
    /// </summary>
    public uint PreviewHairColor { get; set; } = 0xFF2A2D45;

    /// <summary> Preview-only hair highlight color, see <see cref="PreviewHairColor"/>. </summary>
    public uint PreviewHairHighlight { get; set; } = 0xFFC8B09A;


    /// <summary> Debug tunables for the empirical mask-map finish semantics, see ModGeneration.FinishMapping. </summary>
    public int MaskRoughnessChannel { get; set; } = 1;

    public bool MaskInvertRoughness { get; set; } = false;

    public bool MaskWriteSpec { get; set; } = false;
    
    [JsonConverter(typeof(SortModeConverter))]
    [JsonProperty(Order = int.MaxValue)]
    public ISortMode SortMode { get; set; } = ISortMode.FoldersFirst;
    
#if DEBUG
    public bool DebugMode { get; set; } = true;
#else
    public bool DebugMode { get; set; } = false;
#endif
    
    public int Version { get; set; } = Constants.CurrentVersion;
    
    public Dictionary<ColorId, uint> Colors { get; private set; }
        = Enum.GetValues<ColorId>().ToDictionary(c => c, c => c.Data().DefaultColor);
    
    [JsonIgnore] private readonly SaveService _saveService;

    public Configuration(SaveService saveService)
    {
        _saveService = saveService;
        Load();
    }
    
    public void Save() => _saveService.DelaySave(this);

    private void Load()
    {
        if (!File.Exists(_saveService.FileNames.ConfigFile))
            return;

        try
        {
            var text = File.ReadAllText(_saveService.FileNames.ConfigFile);
            JsonConvert.PopulateObject(text, this, new JsonSerializerSettings {
                Error = HandleDeserializationError,
            });
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Error reading Configuration: {ex.Message}");
        }

        static void HandleDeserializationError(object? sender, ErrorEventArgs errorArgs)
        {
            DynamicTextureManager.Log.Error($"Error parsing Configuration at {errorArgs.ErrorContext.Path}");
            errorArgs.ErrorContext.Handled = true;
        }
    }
    
    public string ToFilePath(FilenameService fileNames) => fileNames.ConfigFile;

    [JsonIgnore] private string? _snapshot;

    /// <summary> Serialize on the calling thread — the save service writes on a background task. </summary>
    internal void CaptureSnapshot()
        => _snapshot = JsonConvert.SerializeObject(this, Formatting.Indented);

    public void Save(Stream stream)
    {
        using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(_snapshot ?? JsonConvert.SerializeObject(this, Formatting.Indented));
    }
    

    public static class Constants
    {
        public const int CurrentVersion = 1;

        public static readonly ISortMode[] ValidSortModes =
        [
            ISortMode.FoldersFirst,
            ISortMode.Lexicographical,
            new DTextureFileSystem.CreationDate(),
            new DTextureFileSystem.InverseCreationDate(),
            new DTextureFileSystem.UpdateDate(),
            new DTextureFileSystem.InverseUpdateDate(),
            ISortMode.InverseFoldersFirst,
            ISortMode.InverseLexicographical,
            ISortMode.FoldersLast,
            ISortMode.InverseFoldersLast,
            ISortMode.InternalOrder,
            ISortMode.InverseInternalOrder,
        ];

        /// <summary> Find a sort mode by its stored type name, also accepting the old OtterGui names with their trailing 'T'. </summary>
        public static ISortMode? ParseSortMode(string name)
        {
            var mode = ValidSortModes.FirstOrDefault(s => s.GetType().Name == name);
            if (mode == null && name.EndsWith('T'))
                mode = ValidSortModes.FirstOrDefault(s => s.GetType().Name == name[..^1]);
            return mode;
        }
    }

    /// <summary>
    /// Reads both Luna's flat modifier shape and OtterGui's old nested one
    /// ({"Modifier1": {"Modifier": 17}, ...}), so customized delete modifiers survive
    /// the migration; writes Luna's flat shape.
    /// </summary>
    private class DoubleModifierCompatConverter : JsonConverter<DoubleModifier>
    {
        public override void WriteJson(JsonWriter writer, DoubleModifier value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Modifier1");
            writer.WriteValue((ushort)value.Modifier1.Modifier);
            if (value.Modifier2.Modifier != ModifierHotkey.NoKey)
            {
                writer.WritePropertyName("Modifier2");
                writer.WriteValue((ushort)value.Modifier2.Modifier);
            }

            writer.WriteEndObject();
        }

        public override DoubleModifier ReadJson(JsonReader reader, Type objectType, DoubleModifier existingValue, bool hasExistingValue,
            JsonSerializer serializer)
        {
            var token = JToken.ReadFrom(reader);
            var m1    = ReadKey(token["Modifier1"]);
            var m2    = ReadKey(token["Modifier2"]);
            return new DoubleModifier(new ModifierHotkey(m1), new ModifierHotkey(m2));

            static VirtualKey ReadKey(JToken? value)
                => value switch
                {
                    JObject nested => (VirtualKey)(nested["Modifier"]?.ToObject<ushort>() ?? 0),
                    JValue         => (VirtualKey)(value.ToObject<ushort?>() ?? 0),
                    _              => VirtualKey.NO_KEY,
                };
        }
    }

    private class SortModeConverter : JsonConverter<ISortMode>
    {
        public override void WriteJson(JsonWriter writer, ISortMode? value, JsonSerializer serializer)
        {
            value ??= ISortMode.FoldersFirst;
            serializer.Serialize(writer, value.GetType().Name);
        }

        public override ISortMode ReadJson(JsonReader reader, Type objectType, ISortMode? existingValue,
            bool hasExistingValue,
            JsonSerializer serializer)
        {
            var name = serializer.Deserialize<string>(reader);
            if (name == null)
                return existingValue ?? ISortMode.FoldersFirst;

            return Constants.ParseSortMode(name) ?? existingValue ?? ISortMode.FoldersFirst;
        }
    }
}
