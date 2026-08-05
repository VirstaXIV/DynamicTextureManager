namespace DynamicTextureManager.DTextures.History;

public readonly record struct CreationTransaction(string? Path)
    : ITransaction;
