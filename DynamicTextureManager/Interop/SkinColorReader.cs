using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using OtterGui.Services;
using Penumbra.GameData.Enums;
using Penumbra.GameData.Files;
using Penumbra.GameData.Interop;

namespace DynamicTextureManager.Interop;

/// <summary>
/// Reads the local player's actual configured skin color (customize data + the game's
/// human.cmp color table), so the 3D preview can use the character's real skin tone instead of
/// a manual guess — the preview then reads as "your character wearing just their smallclothes"
/// rather than an arbitrary tint. Non-human / not loaded / unreadable cmp all degrade to
/// <c>false</c>; callers keep whatever tone they already have (typically the manual override).
/// </summary>
/// <remarks>
/// Deliberately does NOT reinterpret the whole ~180KB <see cref="CmpData"/> struct via
/// MemoryMarshal.Read, and never indexes its <c>[InlineArray]</c> fields (Races[], the 256-entry
/// color palettes) directly — InlineArray indexers skip bounds checks by design, so an
/// out-of-range index there is an unsafe out-of-bounds memory read: an access violation, which
/// is NOT catchable by try/catch and crashed the game outright (2026-07, twice — once from an
/// unvalidated race index, and again even after that was fixed, most likely from the whole-
/// struct reinterpretation itself never having been verified byte-for-byte against the real
/// file). Instead the exact byte offset of the single color needed is computed from the
/// struct's own component sizes (<see cref="Unsafe.SizeOf{T}"/>, so it tracks the real managed
/// layout rather than a hand-typed constant) and read through a normal, bounds-checked
/// <c>byte[]</c> indexer — any layout surprise now produces a wrong color or a caught exception,
/// never a crash.
/// </remarks>
public sealed unsafe class SkinColorReader(IObjectTable objects, IDataManager dataManager) : IService
{
    // The cmp file never changes at runtime — bytes cached, re-fetch only retried on failure.
    private byte[]? _cmpBytes;
    private bool    _cmpLoadFailed;

    /// <summary> The local player's configured skin color as 0..1 RGB, if readable. </summary>
    public bool TryGetLocalPlayerSkin(out Vector3 rgb)
    {
        rgb = default;
        try
        {
            var player = objects[0];
            if (player == null)
                return false;

            var gameObject = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
            if (gameObject == null)
                return false;

            // Model.IsHuman walks DrawObject -> CharacterBase -> ModelType, matching the
            // CharacterModelState pattern; a null/non-character/non-human draw object all fall
            // out of this check without needing to be handled separately.
            Model model = gameObject->DrawObject;
            if (!model.IsHuman)
                return false;

            var customize = model.GetCustomize();
            var clan      = customize.Clan;
            var gender    = customize.Gender;
            var skinIndex = customize.Get(CustomizeIndex.SkinColor).Value;

            // Same (clan-1)*2[+1] formula CmpData.Index uses, computed here as a plain int —
            // guards SubRace.Unknown (0) or any unexpected value before it ever becomes a file
            // offset, rather than trusting live customize data is always well-formed.
            if (clan is < SubRace.Midlander or > SubRace.Veena || gender is not (Gender.Male or Gender.Female))
                return false;

            var raceGenderIndex = gender == Gender.Female ? ((int)clan - 1) * 2 + 1 : ((int)clan - 1) * 2;

            var bytes = GetCmpBytes();
            if (bytes == null)
                return false;

            // File layout: two ColorParameters blocks, then 32 GenderClanColorParameters
            // blocks (one per race/gender combination), each starting with its 256-color Skin
            // palette — see Penumbra.GameData.Files.CmpData for the authoritative field order.
            var racesOffset = Unsafe.SizeOf<CmpData.ColorParameters>() * 2;
            var blockOffset = racesOffset + raceGenderIndex * Unsafe.SizeOf<CmpData.GenderClanColorParameters>();
            var colorOffset = blockOffset + skinIndex * 4;
            if (colorOffset < 0 || colorOffset + 4 > bytes.Length)
                return false;

            rgb = new Vector3(bytes[colorOffset] / 255f, bytes[colorOffset + 1] / 255f, bytes[colorOffset + 2] / 255f);
            return true;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not read the local player's skin color: {ex.Message}");
            return false;
        }
    }

    private byte[]? GetCmpBytes()
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
