using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using OtterGui.Services;
using Penumbra.GameData.Enums;
using Penumbra.GameData.Files;
using Penumbra.GameData.Interop;

namespace DynamicTextureManager.Interop;

/// <summary> The local player's configured hair colors as 0..1 RGB, plus the highlights toggle. </summary>
public readonly record struct HairColors(Vector3 Main, Vector3 Highlight, bool HighlightsEnabled);

/// <summary>
/// Reads the local player's actual configured hair and highlight colors (customize data + the
/// game's human.cmp color table), so the 3D preview can blend the character's real colors by
/// the hair normal map's blue channel instead of manual guesses. Non-human / not loaded /
/// unreadable cmp all degrade to <c>false</c>; callers keep whatever colors they already have.
/// </summary>
/// <remarks>
/// Follows <see cref="SkinColorReader"/>'s hard rule: never reinterpret <see cref="CmpData"/>
/// via MemoryMarshal or index its <c>[InlineArray]</c> fields — InlineArray indexers skip bounds
/// checks, so a bad index is an uncatchable access violation (crashed the game twice, 2026-07).
/// The single colors needed are read at byte offsets computed from the struct's own component
/// sizes through a bounds-checked <c>byte[]</c> indexer.
/// </remarks>
public sealed unsafe class HairColorReader(IObjectTable objects, CmpFileCache cmpCache) : IService
{
    public bool TryGetLocalPlayerHair(out HairColors result)
    {
        result = default;
        try
        {
            var player = objects[0];
            if (player == null)
                return false;

            var gameObject = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
            if (gameObject == null)
                return false;

            Model model = gameObject->DrawObject;
            if (!model.IsHuman)
                return false;

            var customize      = model.GetCustomize();
            var clan           = customize.Clan;
            var gender         = customize.Gender;
            var hairIndex      = customize.Get(CustomizeIndex.HairColor).Value;
            var highlightIndex = customize.Get(CustomizeIndex.HighlightsColor).Value;
            var highlightsOn   = customize.Get(CustomizeIndex.Highlights).Value != 0;

            // Same validation as SkinColorReader — guard live customize data before any value
            // becomes a file offset.
            if (clan is < SubRace.Midlander or > SubRace.Veena || gender is not (Gender.Male or Gender.Female))
                return false;

            var raceGenderIndex = gender == Gender.Female ? ((int)clan - 1) * 2 + 1 : ((int)clan - 1) * 2;

            var bytes = cmpCache.GetCmpBytes();
            if (bytes == null)
                return false;

            // File layout (see Penumbra.GameData.Files.CmpData): two ColorParameters blocks,
            // then 32 GenderClanColorParameters blocks. The per-race/gender hair palette sits
            // after that block's 256-color Skin palette, one 8-byte HairColor (Main + unused
            // sheen) per entry. The highlight palette is shared: the FIRST ColorParameters
            // block's HairHighlights, right after its 256-color Eyes palette.
            var racesOffset = Unsafe.SizeOf<CmpData.ColorParameters>() * 2;
            var blockOffset = racesOffset + raceGenderIndex * Unsafe.SizeOf<CmpData.GenderClanColorParameters>();
            var mainOffset  = blockOffset + Unsafe.SizeOf<CmpData.FullColors>() + hairIndex * Unsafe.SizeOf<CmpData.HairColor>();
            var highlightOffset = Unsafe.SizeOf<CmpData.FullColors>() + highlightIndex * 4;
            if (mainOffset < 0 || mainOffset + 4 > bytes.Length || highlightOffset + 4 > bytes.Length)
                return false;

            var main = new Vector3(bytes[mainOffset] / 255f, bytes[mainOffset + 1] / 255f, bytes[mainOffset + 2] / 255f);
            var highlight = new Vector3(bytes[highlightOffset] / 255f, bytes[highlightOffset + 1] / 255f,
                bytes[highlightOffset + 2] / 255f);
            result = new HairColors(main, highlight, highlightsOn);
            return true;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not read the local player's hair colors: {ex.Message}");
            return false;
        }
    }
}
