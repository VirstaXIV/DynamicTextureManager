using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using OtterGui.Services;

namespace DynamicTextureManager.DTextures;

public class DTextureStorage : List<DTexture>, IService
{
    public bool TryGetValue(Guid identifier, [NotNullWhen(true)] out DTexture? dTexture)
    {
        dTexture = ByIdentifier(identifier);
        return dTexture != null;
    }

    public DTexture? ByIdentifier(Guid identifier)
        => this.FirstOrDefault(d => d.Identifier == identifier);

    public bool Contains(Guid identifier)
        => ByIdentifier(identifier) != null;
}