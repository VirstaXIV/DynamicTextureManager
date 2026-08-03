namespace DynamicTextureManager.DTextures.History;

public readonly record struct CreationTransaction(string Name, string? Path)
    : ITransaction;

public readonly record struct RenameTransaction(string Old, string New)
    : ITransaction;
