using Dalamud.Configuration;
using System;
using Dalamud.Plugin;

namespace DynamicTextureManager;

[Serializable]
public class Configuration(IDalamudPluginInterface pluginInterface) : IPluginConfiguration
{
    private readonly IDalamudPluginInterface _pluginInterface = pluginInterface;
    
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool SomePropertyToBeSavedAndWithADefault { get; set; } = true;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        _pluginInterface.SavePluginConfig(this);
    }
}
