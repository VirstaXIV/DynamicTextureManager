using System;
using System.Collections.Generic;
using System.Linq;
using IService = Luna.IService;

namespace DynamicTextureManager.DTextures;

public class DTextureStorage : List<DTexture>, IService
{
    public DTexture? ByIdentifier(Guid identifier)
        => this.FirstOrDefault(d => d.Identifier == identifier);

    public bool Contains(Guid identifier)
        => ByIdentifier(identifier) != null;
}