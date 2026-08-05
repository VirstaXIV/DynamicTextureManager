using System;
using Dalamud.Plugin.Services;
using OtterGui.Services;

namespace DynamicTextureManager.Interop;

/// <summary>
/// Cached raw bytes of the game's human.cmp color table (skin/hair/highlight palettes per
/// race and gender), shared by the customize color readers. The file never changes at
/// runtime — bytes are cached on first success, re-fetch is only retried after a failure.
/// </summary>
public sealed class CmpFileCache(IDataManager dataManager) : IService
{
    private byte[]? _cmpBytes;
    private bool    _cmpLoadFailed;

    public byte[]? GetCmpBytes()
    {
        if (_cmpBytes != null || _cmpLoadFailed)
            return _cmpBytes;

        try
        {
            _cmpBytes = dataManager.GetFile("chara/xls/charamake/human.cmp")?.Data;
            if (_cmpBytes is not { Length: > 0 })
            {
                _cmpBytes      = null;
                _cmpLoadFailed = true;
            }
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not load human.cmp: {ex.Message}");
            _cmpLoadFailed = true;
        }

        return _cmpBytes;
    }
}
