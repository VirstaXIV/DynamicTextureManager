using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.ImGuiNotification;
using DynamicTextureManager.DTextures.History;
using DynamicTextureManager.Events;
using DynamicTextureManager.Services;
using Luna;

namespace DynamicTextureManager.DTextures;

public class DTextureFileSystem : BaseFileSystem, IDisposable
{
    private readonly DTextureFileSystemSaver _saver;
    private readonly DTextureChanged         _dTextureChanged;

    public DTextureFileSystem(LunaLogger log, SaveService saveService, DTextureManager dTextureManager, DTextureChanged dTextureChanged)
        : base("DTextureFileSystem", log, true)
    {
        _dTextureChanged = dTextureChanged;
        _saver           = new DTextureFileSystemSaver(log, this, saveService, dTextureManager.DTextures);

        _saver.Load();
        _dTextureChanged.Subscribe(OnDTextureChange, DTextureChanged.Priority.DTextureFileSystem);
        DynamicTextureManager.Log.Debug("Reloaded dTexture filesystem.");
    }

    public void Dispose()
    {
        _dTextureChanged.Unsubscribe(OnDTextureChange);
        _saver.Dispose();
    }

    public struct CreationDate : ISortMode
    {
        public ReadOnlySpan<byte> Name
            => "Creation Date (Older First)"u8;

        public ReadOnlySpan<byte> Description
            => "In each folder, sort all subfolders lexicographically, then sort all leaves using their creation date."u8;

        public IEnumerable<IFileSystemNode> GetChildren(IFileSystemFolder f)
            => ISortMode.GetFolderLike(f).Concat(ISortMode.GetLeaveLike(f).OrderBy(CreationDateKey));
    }

    public struct UpdateDate : ISortMode
    {
        public ReadOnlySpan<byte> Name
            => "Update Date (Older First)"u8;

        public ReadOnlySpan<byte> Description
            => "In each folder, sort all subfolders lexicographically, then sort all leaves using their last update date."u8;

        public IEnumerable<IFileSystemNode> GetChildren(IFileSystemFolder f)
            => ISortMode.GetFolderLike(f).Concat(ISortMode.GetLeaveLike(f).OrderBy(UpdateDateKey));
    }

    public struct InverseCreationDate : ISortMode
    {
        public ReadOnlySpan<byte> Name
            => "Creation Date (Newer First)"u8;

        public ReadOnlySpan<byte> Description
            => "In each folder, sort all subfolders lexicographically, then sort all leaves using their inverse creation date."u8;

        public IEnumerable<IFileSystemNode> GetChildren(IFileSystemFolder f)
            => ISortMode.GetFolderLike(f).Concat(ISortMode.GetLeaveLike(f).OrderByDescending(CreationDateKey));
    }

    public struct InverseUpdateDate : ISortMode
    {
        public ReadOnlySpan<byte> Name
            => "Update Date (Newer First)"u8;

        public ReadOnlySpan<byte> Description
            => "In each folder, sort all subfolders lexicographically, then sort all leaves using their inverse last update date."u8;

        public IEnumerable<IFileSystemNode> GetChildren(IFileSystemFolder f)
            => ISortMode.GetFolderLike(f).Concat(ISortMode.GetLeaveLike(f).OrderByDescending(UpdateDateKey));
    }

    private static DateTimeOffset CreationDateKey(IFileSystemNode node)
        => (node as IFileSystemData)?.GetValue<DTexture>()?.CreationDate ?? default;

    private static DateTimeOffset UpdateDateKey(IFileSystemNode node)
        => (node as IFileSystemData)?.GetValue<DTexture>()?.LastEdit ?? default;

    private void OnDTextureChange(in DTextureChanged.Arguments args)
    {
        var (type, dTexture, data) = args;
        switch (type)
        {
            case DTextureChanged.Type.Created:
                var parent = Root;
                var folder = (data as CreationTransaction?)?.Path ?? dTexture.Path.Folder;
                if (folder.Length > 0)
                    try
                    {
                        parent = FindOrCreateAllFolders(folder);
                    }
                    catch (Exception ex)
                    {
                        DynamicTextureManager.Messager.NotificationMessage(ex, $"Could not move canvas group to {folder} because the folder could not be created.",
                            NotificationType.Error);
                    }

                var (node, _) = CreateDuplicateDataNode(parent, dTexture.Path.SortName ?? dTexture.Name, dTexture);
                Selection.Select(node, true);
                return;
            case DTextureChanged.Type.Deleted:
                // The selection tracks node removal itself — no explicit unselect, which
                // would wrongly clear an unrelated multi-selection.
                if (dTexture.Node is { } leaf)
                    Delete(leaf);
                return;
        }
    }
}
