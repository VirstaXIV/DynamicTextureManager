using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Finds the file a Penumbra mod provides for a game path by scanning its option jsons
/// (default_mod.json and group files). Used to recover a texture's pristine source when
/// Penumbra's resolve would return our own generated override instead.
/// </summary>
public static class ModFileLocator
{
    public static string? Find(string modDirectory, string gamePath)
    {
        try
        {
            if (!Directory.Exists(modDirectory))
                return null;

            foreach (var jsonFile in Directory.EnumerateFiles(modDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(jsonFile).Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? relative = null;
                try
                {
                    relative = FindInToken(JObject.Parse(File.ReadAllText(jsonFile)), gamePath);
                }
                catch (Exception ex)
                {
                    DynamicTextureManager.Log.Debug($"Could not parse {jsonFile}: {ex.Message}");
                }

                if (relative == null)
                    continue;

                var full = Path.Combine(modDirectory,
                    relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                    return full;
            }
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not search mod {modDirectory} for {gamePath}: {ex.Message}");
        }

        return null;
    }

    /// <summary> Recursively find any "Files" mapping containing the game path — covers default_mod and all group/option layouts. </summary>
    private static string? FindInToken(JToken token, string gamePath)
    {
        switch (token)
        {
            case JObject obj:
            {
                if (obj["Files"] is JObject files)
                    foreach (var property in files.Properties())
                        if (string.Equals(property.Name, gamePath, StringComparison.OrdinalIgnoreCase))
                            return property.Value.ToObject<string>();

                foreach (var property in obj.Properties())
                {
                    if (property.Name == "Files")
                        continue;

                    if (FindInToken(property.Value, gamePath) is { } found)
                        return found;
                }

                return null;
            }
            case JArray array:
            {
                foreach (var item in array)
                    if (FindInToken(item, gamePath) is { } found)
                        return found;

                return null;
            }
            default:
                return null;
        }
    }
}
