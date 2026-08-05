using DynamicTextureManager.DTextures;
using DynamicTextureManager.Events;
using ImSharp;
using Luna;

namespace DynamicTextureManager.UI;

public sealed class DTMFileSystemCache : FileSystemCache<DTMFileSystemCache.DTextureNode>
{
    public DTMFileSystemCache(DTMFileSystemDrawer parent)
        : base(parent)
    {
        parent.DTextureChanged.Subscribe(OnDTextureChanged, DTextureChanged.Priority.DTMFileSystemCache);
    }

    private new DTMFileSystemDrawer Parent
        => (DTMFileSystemDrawer)base.Parent;

    private void OnDTextureChanged(in DTextureChanged.Arguments arguments)
    {
        VisibleDirty = true;
        if (arguments.DTexture.Node is { } node && AllNodes.TryGetValue(node, out var cache))
            cache.Dirty = true;
    }

    protected override DTextureNode ConvertNode(in IFileSystemNode node)
        => new((IFileSystemData<DTexture>)node);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Parent.DTextureChanged.Unsubscribe(OnDTextureChanged);
    }

    public sealed class DTextureNode(IFileSystemData<DTexture> node) : BaseFileSystemNodeCache<DTextureNode>
    {
        public readonly IFileSystemData<DTexture> Node = node;

        /// <summary> Gray out entries whose generated mod is currently disabled in Penumbra. </summary>
        protected override void DrawInternal(FileSystemCache<DTextureNode> cache, IFileSystemNode node)
        {
            var c        = (DTMFileSystemCache)cache;
            var disabled = c.Parent.OverlayMods.IsModEnabled(Node.Value) == false;

            using var color = ImGuiColor.Text.Push(new Rgba32(ColorId.DisabledMod.Value()), disabled);
            var flags = node.Selected ? TreeNodeFlags.NoTreePushOnOpen | TreeNodeFlags.Selected : TreeNodeFlags.NoTreePushOnOpen;
            Im.Tree.Leaf(node.Name, flags);
            if (disabled)
                Im.Tooltip.OnHover("The generated mod of this canvas group is currently disabled in Penumbra."u8);
        }
    }
}
