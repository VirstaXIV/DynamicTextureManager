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
    public bool OpenFoldersByDefault { get; set; } = false;
    public bool AutoReload { get; set; } = true;
    public int OverlayPriority { get; set; } = 999;
    public bool LivePreview { get; set; } = true;
    public bool DeleteModWithDTexture { get; set; } = true;
    public int DefaultDecalMaxColors { get; set; } = 6;
    public bool ShowUvSeams { get; set; } = true;
    public Guid SelectedDTexture { get; set; } = Guid.Empty;
    public float CurrentDTextureSelectorWidth { get; set; } = 200f;
    public float DTMSelectorMinimumScale { get; set; } = 0.1f;
    public float DTMSelectorMaximumScale { get; set; } = 0.5f;
    public DoubleModifier DeleteDTextureModifier { get; set; } = new(ModifierHotkey.Control, ModifierHotkey.Shift);

    /// <summary> Folder decal images are stored in; empty uses the default inside the plugin config directory. </summary>
    public string DecalStorageFolder { get; set; } = string.Empty;

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
    
    public void SaveNow() => _saveService.ImmediateSave(this);
    
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
    
    public string ToFilename(FilenameService fileNames) => fileNames.ConfigFile;

    public void Save(StreamWriter writer)
    {
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
