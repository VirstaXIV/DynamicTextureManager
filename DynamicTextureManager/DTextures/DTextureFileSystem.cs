using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Interface.ImGuiNotification;
using DynamicTextureManager.DTextures.History;
using DynamicTextureManager.Events;
using DynamicTextureManager.Services;
using OtterGui.Classes;
using OtterGui.Filesystem;

namespace DynamicTextureManager.DTextures;

public class DTextureFileSystem : FileSystem<DTexture>, IDisposable, ISavable
{
    private readonly DTextureChanged _dTextureChanged;
    
    private readonly SaveService   _saveService;
    private readonly DTextureManager _dTextureManager;

    public DTextureFileSystem(DTextureManager dTextureManager, SaveService saveService, DTextureChanged dTextureChanged)
    {
        _dTextureManager = dTextureManager;
        _saveService   = saveService;
        _dTextureChanged = dTextureChanged;
        _dTextureChanged.Subscribe(OnDTextureChange, DTextureChanged.Priority.DTextureFileSystem);
        Reload();
    }
    
    private void Reload()
    {
        if (Load(new FileInfo(_saveService.FileNames.DTextureFileSystem), _dTextureManager.DTextures, DTextureToIdentifier, DTextureToName))
            _saveService.ImmediateSave(this);

        DynamicTextureManager.Log.Debug("Reloaded dTexture filesystem.");
    }
    
    public void Dispose()
    {
        _dTextureChanged.Unsubscribe(OnDTextureChange);
    }
    
    public struct CreationDate : ISortMode<DTexture>
    {
        public ReadOnlySpan<byte> Name
            => "Creation Date (Older First)"u8;

        public ReadOnlySpan<byte> Description
            => "In each folder, sort all subfolders lexicographically, then sort all leaves using their creation date."u8;

        public IEnumerable<IPath> GetChildren(Folder f)
            => f.GetSubFolders().Cast<IPath>().Concat(f.GetLeaves().OrderBy(l => l.Value.CreationDate));
    }

    public struct UpdateDate : ISortMode<DTexture>
    {
        public ReadOnlySpan<byte> Name
            => "Update Date (Older First)"u8;

        public ReadOnlySpan<byte> Description
            => "In each folder, sort all subfolders lexicographically, then sort all leaves using their last update date."u8;

        public IEnumerable<IPath> GetChildren(Folder f)
            => f.GetSubFolders().Cast<IPath>().Concat(f.GetLeaves().OrderBy(l => l.Value.LastEdit));
    }
    
    public struct InverseCreationDate : ISortMode<DTexture>
    {
        public ReadOnlySpan<byte> Name
            => "Creation Date (Newer First)"u8;

        public ReadOnlySpan<byte> Description
            => "In each folder, sort all subfolders lexicographically, then sort all leaves using their inverse creation date."u8;

        public IEnumerable<IPath> GetChildren(Folder f)
            => f.GetSubFolders().Cast<IPath>().Concat(f.GetLeaves().OrderByDescending(l => l.Value.CreationDate));
    }

    public struct InverseUpdateDate : ISortMode<DTexture>
    {
        public ReadOnlySpan<byte> Name
            => "Update Date (Newer First)"u8;

        public ReadOnlySpan<byte> Description
            => "In each folder, sort all subfolders lexicographically, then sort all leaves using their inverse last update date."u8;

        public IEnumerable<IPath> GetChildren(Folder f)
            => f.GetSubFolders().Cast<IPath>().Concat(f.GetLeaves().OrderByDescending(l => l.Value.LastEdit));
    }
    
    private void OnDTextureChange(DTextureChanged.Type type, DTexture dTexture, ITransaction? data)
    {
        switch (type)
        {
            case DTextureChanged.Type.Created:
                var parent = Root;
                if ((data as CreationTransaction?)?.Path is { } path)
                    try
                    {
                        parent = FindOrCreateAllFolders(path);
                    }
                    catch (Exception ex)
                    {
                        DynamicTextureManager.Messager.NotificationMessage(ex, $"Could not move dTexture to {path} because the folder could not be created.",
                            NotificationType.Error);
                    }

                CreateDuplicateLeaf(parent, dTexture.Name.Text, dTexture);

                return;
            case DTextureChanged.Type.Deleted:
                if (TryGetValue(dTexture, out var leaf1))
                    Delete(leaf1);
                return;
            case DTextureChanged.Type.ReloadedAll:
                Reload();
                return;
            case DTextureChanged.Type.Renamed when (data as RenameTransaction?)?.Old is { } oldName:
                if (!TryGetValue(dTexture, out var leaf2))
                    return;

                var old = oldName.FixName();
                if (old == leaf2.Name || leaf2.Name.IsDuplicateName(out var baseName, out _) && baseName == old)
                    RenameWithDuplicates(leaf2, dTexture.Name);
                return;
        }
    }
    
    private static string DTextureToIdentifier(DTexture dTexture)
        => dTexture.Identifier.ToString();

    private static string DTextureToName(DTexture dTexture)
        => dTexture.Name.Text.FixName();
    
    private static bool DesignHasDefaultPath(DTexture dTexture, string fullPath)
    {
        var regex = new Regex($@"^{Regex.Escape(DTextureToName(dTexture))}( \(\d+\))?$");
        return regex.IsMatch(fullPath);
    }
    
    private static (string, bool) SaveDTexture(DTexture dTexture, string fullPath)
        // Only save pairs with non-default paths.
        => DesignHasDefaultPath(dTexture, fullPath)
            ? (string.Empty, false)
            : (DTextureToIdentifier(dTexture), true);
    
    public string ToFilename(FilenameService fileNames)
        => fileNames.DTextureFileSystem;

    public void Save(StreamWriter writer)
    {
        SaveToFile(writer, SaveDTexture, true);
    }
}