using DynamicTextureManager.DTextures;
using DynamicTextureManager.DTextures.History;
using OtterGui.Classes;

namespace DynamicTextureManager.Events;

/// <summary>
/// Triggered when a DTexture is edited in any way.
/// <list type="number">
///     <item>Parameter is the type of the change </item>
///     <item>Parameter is the changed DTexture. </item>
///     <item>Parameter is any additional data depending on the type of change. </item>
/// </list>
/// </summary>
public sealed class DTextureChanged() : EventWrapper<DTextureChanged.Type, DTexture, ITransaction?, DTextureChanged.Priority>(nameof(DTextureChanged))
{
    public enum Type
    {
        /// <summary> A new dTexture was created. </summary>
        Created,
        
        /// <summary> An existing dTexture was deleted. </summary>
        Deleted,
        
        /// <summary> Invoked on full reload. </summary>
        ReloadedAll,

        /// <summary> An existing dTexture was renamed. </summary>
        Renamed,
    }

    public enum Priority
    {
        /// <seealso cref="DTextureFileSystem.OnDesignChange"/>
        DTextureFileSystem = 0,

        /// <seealso cref="UI.DTMFileSystemSelector.OnDesignChange"/>
        DTMFileSystemSelector = -1,

        /// <seealso cref="ModGeneration.OverlayModManager.OnDTextureChanged"/>
        OverlayModManager = -2,
    }
}