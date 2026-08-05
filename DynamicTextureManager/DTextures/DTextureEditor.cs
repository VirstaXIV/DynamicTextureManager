using DynamicTextureManager.Events;
using DynamicTextureManager.Services;

namespace DynamicTextureManager.DTextures;

public class DTextureEditor(
    SaveService saveService,
    DTextureChanged dTextureChanged)
{
    protected readonly DTextureChanged DTextureChanged  = dTextureChanged;
    protected readonly SaveService SaveService = saveService;
}