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
    
    #region Application Data
    
    private bool _writeProtected;
    public bool WriteProtected()
        => _writeProtected;
    
    public bool SetWriteProtected(bool value)
    {
        if (value == _writeProtected)
            return false;

        _writeProtected = value;
        return true;
    }

    #endregion
}