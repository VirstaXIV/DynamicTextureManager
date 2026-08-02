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
/// Reads the local player's actual hair and highlight colors — primarily from the draw
/// object's customize-parameter constant buffer (what the shader consumes, so Glamourer's
/// advanced RGB dyes are included), falling back to customize data + the game's human.cmp
/// color table. Non-human / not loaded / unreadable all degrade to <c>false</c>; callers
/// keep whatever colors they already have.
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

            // Preferred source: the draw object's customize-parameter constant buffer — the
            // exact colors the shader consumes, INCLUDING Glamourer's advanced (RGB) dyes,
            // which never touch the customize bytes and are invisible to the palette lookup
            // below. Buffer values live in the squared domain; take the root back to the
            // palette domain this type promises. NOT trusted blindly: an all-zero read means
            // the buffer wasn't usable on this setup (real palette colors are never exact
            // zero) — fall through to the palette instead of reporting black hair.
            var cbuffer = model.AsHuman->CustomizeParameterCBuffer;
            if (cbuffer != null)
            {
                var parameters = cbuffer->TryGetBuffer<FFXIVClientStructs.FFXIV.Shader.CustomizeParameter>();
                if (parameters.Length > 0)
                {
                    var bufMain = parameters[0].MainColor;
                    var bufMesh = parameters[0].MeshColor;
                    DynamicTextureManager.Log.Verbose(
                        $"Hair cbuffer read: main=({bufMain.X:F3},{bufMain.Y:F3},{bufMain.Z:F3}) mesh=({bufMesh.X:F3},{bufMesh.Y:F3},{bufMesh.Z:F3})");
                    if (bufMain.X + bufMain.Y + bufMain.Z + bufMesh.X + bufMesh.Y + bufMesh.Z > 0.001f)
                    {
                        result = new HairColors(SqrtColor(bufMain), SqrtColor(bufMesh), highlightsOn);
                        return true;
                    }
                }
                else
                {
                    DynamicTextureManager.Log.Verbose("Hair cbuffer read: empty buffer — using the palette.");
                }
            }
            else
            {
                DynamicTextureManager.Log.Verbose("Hair cbuffer read: no buffer — using the palette.");
            }

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

    private static Vector3 SqrtColor(FFXIVClientStructs.FFXIV.Common.Math.Vector3 squared)
        => new(MathF.Sqrt(Math.Clamp(squared.X, 0f, 1f)), MathF.Sqrt(Math.Clamp(squared.Y, 0f, 1f)),
            MathF.Sqrt(Math.Clamp(squared.Z, 0f, 1f)));
}
