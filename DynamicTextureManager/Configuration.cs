using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Configuration;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.Services;
using DynamicTextureManager.UI;
using Newtonsoft.Json;
using OtterGui.Classes;
using OtterGui.Extensions;
using OtterGui.Filesystem;
using ErrorEventArgs = Newtonsoft.Json.Serialization.ErrorEventArgs;

namespace DynamicTextureManager;

public class Configuration: IPluginConfiguration, ISavable
{
    public bool AutoReload { get; set; } = true;
    public int OverlayPriority { get; set; } = 999;
    public bool DeleteModWithDTexture { get; set; } = true;
    public int DefaultDecalMaxColors { get; set; } = 6;
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
    public ISortMode<DTexture> SortMode { get; set; } = ISortMode<DTexture>.FoldersFirst;
    
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

    public void Save(Stream stream)
    {
        using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        using var jWriter = new JsonTextWriter(writer);
        jWriter.Formatting = Formatting.Indented;
        var serializer = new JsonSerializer {
            Formatting = Formatting.Indented
        };
        serializer.Serialize(jWriter, this);
    }
    

    public static class Constants
    {
        public const int CurrentVersion = 1;
        
        public static readonly ISortMode<DTexture>[] ValidSortModes =
        [
            ISortMode<DTexture>.FoldersFirst,
            ISortMode<DTexture>.Lexicographical,
            new DTextureFileSystem.CreationDate(),
            new DTextureFileSystem.InverseCreationDate(),
            new DTextureFileSystem.UpdateDate(),
            new DTextureFileSystem.InverseUpdateDate(),
            ISortMode<DTexture>.InverseFoldersFirst,
            ISortMode<DTexture>.InverseLexicographical,
            ISortMode<DTexture>.FoldersLast,
            ISortMode<DTexture>.InverseFoldersLast,
            ISortMode<DTexture>.InternalOrder,
            ISortMode<DTexture>.InverseInternalOrder,
        ];
    }
    
    private class SortModeConverter : JsonConverter<ISortMode<DTexture>>
    {
        public override void WriteJson(JsonWriter writer, ISortMode<DTexture>? value, JsonSerializer serializer)
        {
            value ??= ISortMode<DTexture>.FoldersFirst;
            serializer.Serialize(writer, value.GetType().Name);
        }

        public override ISortMode<DTexture> ReadJson(JsonReader reader, Type objectType, ISortMode<DTexture>? existingValue,
            bool hasExistingValue,
            JsonSerializer serializer)
        {
            var name = serializer.Deserialize<string>(reader);
            if (name == null || !Constants.ValidSortModes.FindFirst(s => s.GetType().Name == name, out var mode))
                return existingValue ?? ISortMode<DTexture>.FoldersFirst;

            return mode;
        }
    }
}
