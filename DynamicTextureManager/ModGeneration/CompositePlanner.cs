using System;
using System.Collections.Generic;
using System.Linq;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration.Shaders;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Shared discovery of what a composite has to include, used by both the mod build and the
/// preview cache so previews stay pixel-identical to built files.
/// </summary>
public static class CompositePlanner
{
    /// <summary> A sibling texture (normal/mask) that decal material effects replay onto. </summary>
    public sealed record SiblingEffectTarget(string GamePath, TextureSlot Slot, List<TextureLayer> Layers, SourcePath Owner)
    {
        /// <summary> Whether any contributing layer is surface-projected and needs mesh geometry. </summary>
        public bool NeedsMesh
            => Layers.OfType<DecalLayer>().Any(l => l.Surface);
    }

    /// <summary> The source material whose shader exposes a given texture game path. </summary>
    public static SourcePath? FindTextureOwner(DTextureData data, string textureGamePath, ShaderHandlerRegistry handlers,
        SourceFileProvider files)
    {
        foreach (var source in data.Source.Materials)
        {
            var mtrl = files.GetMaterial(source, null);
            if (mtrl == null)
                continue;

            if (handlers.For(mtrl).ClassifyTextures(mtrl)
                .Any(info => string.Equals(info.GamePath, textureGamePath, StringComparison.OrdinalIgnoreCase)))
                return source;
        }

        return null;
    }

    /// <summary>
    /// All textures of a material are related: decals with material effects (normal smoothing,
    /// mask finish) replay their footprint onto the material's normal/mask textures, which
    /// usually have no layers of their own. Aggregated per target texture across the dTexture.
    /// </summary>
    public static List<SiblingEffectTarget> SiblingEffectTargets(DTextureData data, ShaderHandlerRegistry handlers,
        SourceFileProvider files)
    {
        var targets = new List<SiblingEffectTarget>();
        foreach (var (gamePath, layers) in data.Textures)
        {
            var effectLayers = layers.OfType<DecalLayer>()
                .Where(l => l.Enabled && l.HasMaterialEffects)
                .Cast<TextureLayer>()
                .ToList();
            if (effectLayers.Count == 0)
                continue;

            var owner = FindTextureOwner(data, gamePath, handlers, files);
            var mtrl  = owner != null ? files.GetMaterial(owner, null) : null;
            if (owner == null || mtrl == null)
                continue;

            foreach (var info in handlers.For(mtrl).ClassifyTextures(mtrl))
            {
                if (info.Slot is not (TextureSlot.Normal or TextureSlot.Mask)
                 || string.Equals(info.GamePath, gamePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Only layers whose effect actually touches this slot.
                var slotLayers = effectLayers.Where(l => l is DecalLayer d
                     && (info.Slot == TextureSlot.Normal ? d.NormalSmooth > 0f : d.MaskPreset != DecalMaskPreset.Keep))
                    .ToList();
                if (slotLayers.Count == 0)
                    continue;

                var existing = targets.FindIndex(t => string.Equals(t.GamePath, info.GamePath, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                    targets[existing].Layers.AddRange(slotLayers);
                else
                    targets.Add(new SiblingEffectTarget(info.GamePath, info.Slot, slotLayers, owner));
            }
        }

        return targets;
    }
}
