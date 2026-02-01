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

    public FilenameService(IDalamudPluginInterface pi)
    {
        ConfigDirectory        = pi.ConfigDirectory.FullName;
        ConfigFile             = pi.ConfigFile.FullName;
        DTextureFileSystem       = Path.Combine(ConfigDirectory, "sort_order.json");
        MigrationDTextureFile    = Path.Combine(ConfigDirectory, "textures.json");
        DTextureDirectory        = Path.Combine(ConfigDirectory, "textures");
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