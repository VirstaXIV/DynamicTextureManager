using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using DynamicTextureManager.DTextures;
using Luna;

namespace DynamicTextureManager.Services;

public class FilenameService : BaseFilePathProvider
{
    public readonly string ConfigDirectory;
    public readonly string ConfigFile;
    public readonly string DTextureDirectory;
    public readonly string DecalDirectory;
    public readonly string DecalIndexFile;
    public readonly string ExtractedDirectory;
    public readonly string FileSystemOrganization;
    public readonly string FileSystemExpandedFolders;
    public readonly string FileSystemLockedNodes;
    public readonly string FileSystemSelectedNodes;
    public readonly string MigrationDTextureFileSystem;

    public FilenameService(IDalamudPluginInterface pi)
        : base(pi)
    {
        ConfigDirectory        = pi.ConfigDirectory.FullName;
        ConfigFile             = pi.ConfigFile.FullName;
        DTextureDirectory        = Path.Combine(ConfigDirectory, "textures");
        DecalDirectory           = Path.Combine(ConfigDirectory, "decals");
        DecalIndexFile           = Path.Combine(ConfigDirectory, "decals.json");
        ExtractedDirectory       = Path.Combine(ConfigDirectory, "extracted");
        FileSystemOrganization    = Path.Combine(ConfigDirectory, "filesystem", "organization.json");
        FileSystemExpandedFolders = Path.Combine(ConfigDirectory, "filesystem", "expanded_folders.json");
        FileSystemLockedNodes     = Path.Combine(ConfigDirectory, "filesystem", "locked_nodes.json");
        FileSystemSelectedNodes   = Path.Combine(ConfigDirectory, "filesystem", "selected_nodes.json");
        // The old OtterGui file system save; kept as the migration source for existing folder organization.
        MigrationDTextureFileSystem = Path.Combine(ConfigDirectory, "sort_order.json");
    }

    public override List<IBackupFile> GetBackupFiles()
        => [];

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
