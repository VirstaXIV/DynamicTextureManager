using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.DTextures;

public class DTextureBase
{
    public const int FileVersion = 1;
    
    private DTextureData _dTextureData = new();
    
    internal DTextureBase()
    {
        //
    }
    
    internal DTextureBase(DTextureBase clone)
    {
        _dTextureData  = clone._dTextureData;
    }
    
    internal void SetDTextureData(in DTextureData other)
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
    
    #region Serialization

    public JObject JsonSerialize()
    {
        var ret = new JObject
        {
            ["FileVersion"] = FileVersion
        };
        return ret;
    }
    
    #endregion
}