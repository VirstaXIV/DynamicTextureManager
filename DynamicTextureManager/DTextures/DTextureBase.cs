namespace DynamicTextureManager.DTextures;

public class DTextureBase
{
    private DTextureData _dTextureData = new();

    /// <summary> The payload of this dTexture. </summary>
    public DTextureData Data
        => _dTextureData;

    internal DTextureBase()
    {
        //
    }

    internal DTextureBase(DTextureBase clone)
    {
        _dTextureData = clone._dTextureData.Clone();
    }

    internal void SetDTextureData(DTextureData other)
    {
        _dTextureData = other;
    }
}