using DynamicTextureManager.DTextures;
using DynamicTextureManager.DTextures.History;
using Luna;

namespace DynamicTextureManager.Events;

/// <summary>
/// Triggered when a DTexture is edited in any way. The arguments carry the type of the
/// change, the changed DTexture, and any additional data depending on the type of change.
/// </summary>
public sealed class DTextureChanged(LunaLogger log)
    : EventBase<DTextureChanged.Arguments, DTextureChanged.Priority>(nameof(DTextureChanged), log)
{
    public readonly record struct Arguments(Type Type, DTexture DTexture, ITransaction? Data);

    public enum Type
    {
        /// <summary> A new dTexture was created. </summary>
        Created,

        /// <summary> An existing dTexture was deleted. </summary>
        Deleted,
    }

    public enum Priority
    {
        /// <seealso cref="DTextureFileSystem.OnDTextureChange"/>
        DTextureFileSystem = 0,

        /// <seealso cref="ModGeneration.OverlayModManager.OnDTextureChanged"/>
        OverlayModManager = -2,
    }

    public void Invoke(Type type, DTexture dTexture, ITransaction? data)
        => Invoke(new Arguments(type, dTexture, data));
}
