using System;
using System.IO;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using OtterGui.Services;
using ObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace DynamicTextureManager.Interop;

/// <summary>
/// Reads the local player's live model state: which submesh attribute variants of a worn
/// equipment model are currently visible. Used so surface decals are stamped onto (and
/// baked for) the gear variant the player actually sees.
/// </summary>
public sealed unsafe class CharacterModelState(IObjectTable objects) : IService
{
    /// <summary>
    /// The currently visible submesh attribute mask for an equipment mdl game path —
    /// everything-enabled when the slot or model cannot be determined.
    /// </summary>
    public uint CurrentAttributeMask(string mdlGamePath)
    {
        // Human draw-object slot layout: 0-4 equipment (met/top/glv/dwn/sho), 5-9 accessories,
        // 10 hair, 11 face, 12 tail/ears. The SlotCount guard below keeps an unexpected layout
        // from ever reading out of bounds — it falls back to everything-visible instead.
        var slot = Path.GetFileNameWithoutExtension(mdlGamePath) switch
        {
            var n when n.EndsWith("_met", StringComparison.OrdinalIgnoreCase) => 0,
            var n when n.EndsWith("_top", StringComparison.OrdinalIgnoreCase) => 1,
            var n when n.EndsWith("_glv", StringComparison.OrdinalIgnoreCase) => 2,
            var n when n.EndsWith("_dwn", StringComparison.OrdinalIgnoreCase) => 3,
            var n when n.EndsWith("_sho", StringComparison.OrdinalIgnoreCase) => 4,
            var n when n.EndsWith("_hir", StringComparison.OrdinalIgnoreCase) => 10,
            _                                                                 => -1,
        };
        if (slot < 0)
            return uint.MaxValue;

        try
        {
            var player = objects[0];
            if (player == null)
                return uint.MaxValue;

            var gameObject = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
            var drawObject = gameObject != null ? gameObject->DrawObject : null;
            if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
                return uint.MaxValue;

            var characterBase = (CharacterBase*)drawObject;
            if (slot >= characterBase->SlotCount)
                return uint.MaxValue;

            var model = characterBase->Models[slot];
            var mask  = model == null ? uint.MaxValue : model->EnabledAttributeIndexMask;
            // A model rendering with zero enabled attributes is implausible — far more
            // likely the read went wrong, so do not filter in that case.
            return mask == 0 ? uint.MaxValue : mask;
        }
        catch
        {
            return uint.MaxValue;
        }
    }
}
