namespace DynamicTextureManager.DTextures.History;

public readonly record struct CreationTransaction(string Name, string? Path)
    : ITransaction
{
    public ITransaction? Merge(ITransaction other)
        => null;

    public void Revert(IDTextureEditor editor, object data)
    { }
}

public readonly record struct RenameTransaction(string Old, string New)
    : ITransaction
{
    public ITransaction? Merge(ITransaction older)
        => older is RenameTransaction other ? new RenameTransaction(other.Old, New) : null;

    public void Revert(IDTextureEditor editor, object data)
        => ((DTextureManager)editor).Rename((DTexture)data, Old);
}