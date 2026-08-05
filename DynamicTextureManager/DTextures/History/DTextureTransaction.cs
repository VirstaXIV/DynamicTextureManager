namespace DynamicTextureManager.DTextures.History;

public readonly record struct CreationTransaction(string Name, string? Path)
    : ITransaction;
