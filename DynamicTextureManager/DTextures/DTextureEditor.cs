using DynamicTextureManager.Events;
using DynamicTextureManager.Services;

namespace DynamicTextureManager.DTextures;

public class DTextureEditor(
    SaveService saveService,
    DTextureChanged dTextureChanged,
    Configuration config) : IDTextureEditor
{
    protected readonly DTextureChanged DTextureChanged  = dTextureChanged;
    protected readonly SaveService SaveService = saveService;
    protected readonly Configuration Config = config;
}