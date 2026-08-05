using System;
using System.Diagnostics.CodeAnalysis;
using DynamicTextureManager.Services;
using Luna;

namespace DynamicTextureManager.DTextures;

public sealed class DTextureFileSystemSaver(LunaLogger log, BaseFileSystem fileSystem, SaveService saveService, DTextureStorage dTextures)
    : FileSystemSaver<SaveService, FilenameService>(log, fileSystem, saveService)
{
    protected override void SaveDataValue(IFileSystemValue value)
    {
        if (value is DTexture dTexture)
            SaveService.QueueSave(dTexture);
    }

    protected override string LockedFile(FilenameService provider)
        => provider.FileSystemLockedNodes;

    protected override string ExpandedFile(FilenameService provider)
        => provider.FileSystemExpandedFolders;

    protected override string OrganizationFile(FilenameService provider)
        => provider.FileSystemOrganization;

    protected override string SelectionFile(FilenameService provider)
        => provider.FileSystemSelectedNodes;

    /// <summary> The old OtterGui sort_order.json; its folder organization migrates into the per-canvas-group paths on first load. </summary>
    protected override string MigrationFile(FilenameService provider)
        => provider.MigrationDTextureFileSystem;

    protected override ISortMode? ParseSortMode(string name)
        => Configuration.Constants.ParseSortMode(name);

    protected override bool GetValueFromIdentifier(ReadOnlySpan<char> identifier, [NotNullWhen(true)] out IFileSystemValue? value)
    {
        if (!Guid.TryParse(identifier, out var guid))
        {
            value = null;
            return false;
        }

        value = dTextures.ByIdentifier(guid);
        return value is not null;
    }

    protected override void CreateDataNodes()
    {
        foreach (var dTexture in dTextures)
        {
            try
            {
                var folder = dTexture.Path.Folder.Length is 0 ? FileSystem.Root : FileSystem.FindOrCreateAllFolders(dTexture.Path.Folder);
                FileSystem.CreateDuplicateDataNode(folder, dTexture.Path.SortName ?? dTexture.Name.Text, dTexture);
            }
            catch (Exception ex)
            {
                Log.Error($"Could not create folder structure for canvas group {dTexture.Name} at path {dTexture.Path.Folder}: {ex}");
            }
        }
    }
}
