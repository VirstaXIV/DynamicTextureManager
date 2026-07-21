using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DynamicTextureManager.DTextures.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OtterGui.Services;
using SixLabors.ImageSharp;

namespace DynamicTextureManager.Services;

/// <summary>
/// Settings stored on a library entry and applied when the decal is attached to gear.
/// Everything remains overridable on the layer afterwards; the preset only seeds it.
/// </summary>
public sealed class DecalPreset
{
    // Colors
    public bool       IdRemap        = true;
    public int        MaxColors      = 6;
    public float      AlphaThreshold = 0.5f;
    public List<uint> PaletteColors  = [];

    // Surface finish
    public float           NormalSmooth;
    public DecalFinishMode Finish = DecalFinishMode.Keep;
    public float           FinishRoughness = 0.5f;
    public float           FinishSpecScale = 1f;
    public float           EffectScale     = 1f;

    // Transform defaults
    public float Opacity = 1f;
    public float ScaleX  = 0.25f;
    public float ScaleY  = 0.25f;
    public float RotationDeg;
    public float WorldWidth  = 0.1f;
    public float WorldHeight = 0.1f;

    public JObject Serialize()
        => new()
        {
            ["IdRemap"]         = IdRemap,
            ["MaxColors"]       = MaxColors,
            ["AlphaThreshold"]  = AlphaThreshold,
            ["PaletteColors"]   = new JArray(PaletteColors),
            ["NormalSmooth"]    = NormalSmooth,
            ["Finish"]          = (int)Finish,
            ["FinishRoughness"] = FinishRoughness,
            ["FinishSpecScale"] = FinishSpecScale,
            ["EffectScale"]     = EffectScale,
            ["Opacity"]         = Opacity,
            ["ScaleX"]          = ScaleX,
            ["ScaleY"]          = ScaleY,
            ["RotationDeg"]     = RotationDeg,
            ["WorldWidth"]      = WorldWidth,
            ["WorldHeight"]     = WorldHeight,
        };

    public static DecalPreset Load(JObject json)
        => new()
        {
            IdRemap         = json["IdRemap"]?.ToObject<bool>() ?? true,
            MaxColors       = json["MaxColors"]?.ToObject<int>() ?? 6,
            AlphaThreshold  = json["AlphaThreshold"]?.ToObject<float>() ?? 0.5f,
            PaletteColors   = json["PaletteColors"]?.ToObject<List<uint>>() ?? [],
            NormalSmooth    = json["NormalSmooth"]?.ToObject<float>() ?? 0f,
            Finish          = (DecalFinishMode)(json["Finish"]?.ToObject<int>() ?? 0),
            FinishRoughness = json["FinishRoughness"]?.ToObject<float>() ?? 0.5f,
            FinishSpecScale = json["FinishSpecScale"]?.ToObject<float>() ?? 1f,
            EffectScale     = json["EffectScale"]?.ToObject<float>() ?? 1f,
            Opacity         = json["Opacity"]?.ToObject<float>() ?? 1f,
            ScaleX          = json["ScaleX"]?.ToObject<float>() ?? 0.25f,
            ScaleY          = json["ScaleY"]?.ToObject<float>() ?? 0.25f,
            RotationDeg     = json["RotationDeg"]?.ToObject<float>() ?? 0f,
            WorldWidth      = json["WorldWidth"]?.ToObject<float>() ?? 0.1f,
            WorldHeight     = json["WorldHeight"]?.ToObject<float>() ?? 0.1f,
        };
}

public sealed class DecalEntry
{
    public Guid           Id;
    public string         Name         = string.Empty;
    public string         OriginalFile = string.Empty;
    public List<string>   Tags         = [];
    public DateTimeOffset CreatedDate;
    public DecalPreset?   Preset;
}

/// <summary>
/// Shared library of decal images (PNG), referenced by id from dTexture layers. Built mods
/// bake the pixels in, so generated mods stay self-contained even if a decal is later removed
/// from the library. Images live in a configurable storage folder (default inside the plugin
/// config directory); the index always stays in the config directory.
/// </summary>
public sealed class DecalLibrary : IService
{
    private readonly FilenameService _filenames;
    private readonly Configuration   _config;
    private readonly List<DecalEntry> _decals = [];

    public IReadOnlyList<DecalEntry> Decals
        => _decals;

    public DecalLibrary(FilenameService filenames, Configuration config)
    {
        _filenames = filenames;
        _config    = config;
        Load();
    }

    /// <summary> The folder decal PNGs are stored in. </summary>
    public string StorageDirectory
        => _config.DecalStorageFolder.Length > 0 ? _config.DecalStorageFolder : _filenames.DecalDirectory;

    public string FilePath(Guid id)
        => Path.Combine(StorageDirectory, $"{id}.png");

    public DecalEntry? Get(Guid id)
        => _decals.FirstOrDefault(d => d.Id == id);

    /// <summary> The distinct tags across all decals, for filter UIs. </summary>
    public List<string> AllTags()
        => _decals.SelectMany(d => d.Tags).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary> Import an image file into the library. Returns the new entry or null on failure. </summary>
    public DecalEntry? Import(string sourcePath)
    {
        try
        {
            var id = Guid.NewGuid();
            Directory.CreateDirectory(StorageDirectory);

            // Normalize any supported input to PNG so layers can rely on one format.
            using (var image = Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(sourcePath))
            {
                image.SaveAsPng(FilePath(id));
            }

            var entry = new DecalEntry
            {
                Id           = id,
                Name         = Path.GetFileNameWithoutExtension(sourcePath),
                OriginalFile = Path.GetFileName(sourcePath),
                CreatedDate  = DateTimeOffset.UtcNow,
            };
            _decals.Add(entry);
            Save();
            return entry;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Could not import decal from {sourcePath}:\n{ex}");
            return null;
        }
    }

    /// <summary> Import a generated in-memory image (e.g. an extracted colorset footprint) into the library. </summary>
    public DecalEntry? ImportGenerated(Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image, string name)
    {
        try
        {
            var id = Guid.NewGuid();
            Directory.CreateDirectory(StorageDirectory);
            image.SaveAsPng(FilePath(id));

            var entry = new DecalEntry
            {
                Id          = id,
                Name        = name,
                CreatedDate = DateTimeOffset.UtcNow,
            };
            _decals.Add(entry);
            Save();
            return entry;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Could not import generated decal \"{name}\":\n{ex}");
            return null;
        }
    }

    public void Rename(Guid id, string newName)
    {
        if (Get(id) is not { } entry)
            return;

        entry.Name = newName;
        Save();
    }

    public void SetTags(Guid id, IEnumerable<string> tags)
    {
        if (Get(id) is not { } entry)
            return;

        entry.Tags = tags.Select(t => t.Trim()).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save();
    }

    public void SetPreset(Guid id, DecalPreset? preset)
    {
        if (Get(id) is not { } entry)
            return;

        entry.Preset = preset;
        Save();
    }

    public void Delete(Guid id)
    {
        if (_decals.RemoveAll(d => d.Id == id) == 0)
            return;

        Save();
        try
        {
            File.Delete(FilePath(id));
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not delete decal file for {id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Move all decal images to a new storage folder. Copies first and only deletes the old
    /// files after everything arrived, so partial failure never loses images. Returns null on
    /// success or a user-facing error message; the setting is only changed on success. The
    /// library reloads against the new folder afterwards, so entries whose image did not make
    /// it are dropped instead of lingering with dead thumbnails.
    /// </summary>
    public string? MoveStorage(string newFolder)
    {
        newFolder = newFolder.Trim();
        var target = newFolder.Length > 0 ? newFolder : _filenames.DecalDirectory;
        var old    = StorageDirectory;
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(old), StringComparison.OrdinalIgnoreCase))
        {
            // Nothing to move — just persist the (possibly re-spelled) setting.
            _config.DecalStorageFolder = newFolder;
            _config.Save();
            return null;
        }

        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception ex)
        {
            return $"Could not create the folder: {ex.Message}";
        }

        var copied = new List<string>();
        foreach (var entry in _decals)
        {
            var source = Path.Combine(old, $"{entry.Id}.png");
            // Consistent with the load-time drop behavior: missing files are skipped.
            if (!File.Exists(source))
                continue;

            var destination = Path.Combine(target, $"{entry.Id}.png");
            try
            {
                File.Copy(source, destination, overwrite: true);
                copied.Add(destination);
            }
            catch (Exception ex)
            {
                foreach (var file in copied)
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Rollback is best-effort; the authoritative copies are still in the old folder.
                    }

                return $"Could not copy {entry.Name}: {ex.Message}";
            }
        }

        _config.DecalStorageFolder = newFolder;
        _config.Save();

        foreach (var entry in _decals)
            try
            {
                File.Delete(Path.Combine(old, $"{entry.Id}.png"));
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Warning($"Could not remove old decal file for {entry.Id}: {ex.Message}");
            }

        _decals.Clear();
        Load();
        return null;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filenames.DecalIndexFile))
                return;

            var json = JObject.Parse(File.ReadAllText(_filenames.DecalIndexFile));
            if (json["Decals"] is not JArray array)
                return;

            foreach (var token in array.OfType<JObject>())
            {
                var id = token["Id"]?.ToObject<Guid>() ?? Guid.Empty;
                if (id == Guid.Empty || !File.Exists(FilePath(id)))
                    continue;

                var created = token["CreatedDate"]?.ToObject<DateTimeOffset?>();
                _decals.Add(new DecalEntry
                {
                    Id           = id,
                    Name         = token["Name"]?.ToObject<string>() ?? id.ToString(),
                    OriginalFile = token["OriginalFile"]?.ToObject<string>() ?? string.Empty,
                    Tags         = token["Tags"]?.ToObject<List<string>>() ?? [],
                    // Pre-library entries get their PNG's file date as a one-time backfill.
                    CreatedDate  = created ?? new DateTimeOffset(File.GetCreationTimeUtc(FilePath(id)), TimeSpan.Zero),
                    Preset       = token["Preset"] is JObject preset ? DecalPreset.Load(preset) : null,
                });
            }
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Could not load decal library:\n{ex}");
        }
    }

    private void Save()
    {
        try
        {
            var json = new JObject
            {
                ["Decals"] = new JArray(_decals.Select(d =>
                {
                    var token = new JObject
                    {
                        ["Id"]           = d.Id,
                        ["Name"]         = d.Name,
                        ["OriginalFile"] = d.OriginalFile,
                        ["Tags"]         = new JArray(d.Tags),
                        ["CreatedDate"]  = d.CreatedDate.ToString("O"),
                    };
                    if (d.Preset != null)
                        token["Preset"] = d.Preset.Serialize();
                    return token;
                })),
            };
            File.WriteAllText(_filenames.DecalIndexFile, json.ToString(Formatting.Indented));
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Could not save decal library:\n{ex}");
        }
    }
}
