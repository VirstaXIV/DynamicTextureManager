using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using DynamicTextureManager.DTextures;

namespace DynamicTextureManager.Services;

public class FilenameService
{
    public readonly string ConfigDirectory;
    public readonly string ConfigFile;
    public readonly string DTextureFileSystem;
    public readonly string MigrationDTextureFile;
    public readonly string DTextureDirectory;
    public readonly string DecalDirectory;
    public readonly string DecalIndexFile;
    public readonly string ExtractedDirectory;

    public FilenameService(IDalamudPluginInterface pi)
    {
        ConfigDirectory        = pi.ConfigDirectory.FullName;
        ConfigFile             = pi.ConfigFile.FullName;
        DTextureFileSystem       = Path.Combine(ConfigDirectory, "sort_order.json");
        MigrationDTextureFile    = Path.Combine(ConfigDirectory, "textures.json");
        DTextureDirectory        = Path.Combine(ConfigDirectory, "textures");
        DecalDirectory           = Path.Combine(ConfigDirectory, "decals");
        DecalIndexFile           = Path.Combine(ConfigDirectory, "decals.json");
        ExtractedDirectory       = Path.Combine(ConfigDirectory, "extracted");
    }

    public string DecalFile(System.Guid id)
        => Path.Combine(DecalDirectory, $"{id}.png");

    /// <summary> The cleaned source copy of a texture whose baked decals were extracted, one per dTexture and game path. </summary>
    public string ExtractedSourceFile(System.Guid dTexture, string gamePath)
    {
        var hash = System.Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(gamePath.ToLowerInvariant())))[..16];
        return Path.Combine(ExtractedDirectory, $"{dTexture:N}_{hash}.png");
    }

    public IEnumerable<FileInfo> DTextures()
    {
        if (!Directory.Exists(DTextureDirectory))
            yield break;

        foreach (var file in Directory.EnumerateFiles(DTextureDirectory, "*.json", SearchOption.TopDirectoryOnly))
            yield return new FileInfo(file);
    }

    public string DTextureFile(string identifier)
        => Path.Combine(DTextureDirectory, $"{identifier}.json");

    public string DTextureFile(DTexture dTexture)
        => DTextureFile(dTexture.Identifier.ToString());
}