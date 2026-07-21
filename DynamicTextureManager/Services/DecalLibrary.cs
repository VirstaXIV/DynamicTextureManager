using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OtterGui.Services;
using SixLabors.ImageSharp;

namespace DynamicTextureManager.Services;

public sealed record DecalEntry(Guid Id, string Name, string OriginalFile);

/// <summary>
/// Shared library of decal images (PNG), stored in the plugin config directory and
/// referenced by id from dTexture layers. Built mods bake the pixels in, so generated
/// mods stay self-contained even if a decal is later removed from the library.
/// </summary>
public sealed class DecalLibrary : IService
{
    private readonly FilenameService _filenames;
    private readonly List<DecalEntry> _decals = [];

    public IReadOnlyList<DecalEntry> Decals
        => _decals;

    public DecalLibrary(FilenameService filenames)
    {
        _filenames = filenames;
        Load();
    }

    public DecalEntry? Get(Guid id)
        => _decals.FirstOrDefault(d => d.Id == id);

    public string FilePath(Guid id)
        => _filenames.DecalFile(id);

    /// <summary> Import an image file into the library. Returns the new entry or null on failure. </summary>
    public DecalEntry? Import(string sourcePath)
    {
        try
        {
            var id     = Guid.NewGuid();
            var target = _filenames.DecalFile(id);
            Directory.CreateDirectory(_filenames.DecalDirectory);

            // Normalize any supported input to PNG so layers can rely on one format.
            using (var image = Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(sourcePath))
            {
                image.SaveAsPng(target);
            }

            var entry = new DecalEntry(id, Path.GetFileNameWithoutExtension(sourcePath), Path.GetFileName(sourcePath));
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
            Directory.CreateDirectory(_filenames.DecalDirectory);
            image.SaveAsPng(_filenames.DecalFile(id));

            var entry = new DecalEntry(id, name, string.Empty);
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
        var index = _decals.FindIndex(d => d.Id == id);
        if (index < 0)
            return;

        _decals[index] = _decals[index] with { Name = newName };
        Save();
    }

    public void Delete(Guid id)
    {
        if (_decals.RemoveAll(d => d.Id == id) == 0)
            return;

        Save();
        try
        {
            File.Delete(_filenames.DecalFile(id));
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not delete decal file for {id}: {ex.Message}");
        }
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
                if (id == Guid.Empty || !File.Exists(_filenames.DecalFile(id)))
                    continue;

                _decals.Add(new DecalEntry(id,
                    token["Name"]?.ToObject<string>() ?? id.ToString(),
                    token["OriginalFile"]?.ToObject<string>() ?? string.Empty));
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
                ["Decals"] = new JArray(_decals.Select(d => new JObject
                {
                    ["Id"]           = d.Id,
                    ["Name"]         = d.Name,
                    ["OriginalFile"] = d.OriginalFile,
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
