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
                var slotLayers = effectLayers
                    .Where(d => info.Slot == TextureSlot.Normal ? d.NormalSmooth > 0f : d.WantsMaskEffect)
                    .Cast<TextureLayer>()
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

    /// <summary>
    /// A body-overlay texture (nails, accents — added as its own source material, see
    /// ModelUvReader.GetBodyOverlayMaterials) whose mesh an enabled body-skin surface decal's
    /// footprint overlaps: that decal's layer(s) should also bake onto this texture, so a
    /// tattoo continues seamlessly across the seam.
    /// </summary>
    public sealed record OverlayCompanionTarget(string GamePath, List<TextureLayer> Layers, SourcePath Owner);

    /// <summary>
    /// Every pair of body-skin-family source materials (the body itself, plus any added overlay
    /// parts) where one's enabled surface decal footprint touches the other's own mesh —
    /// see SurfaceDecalBaker.FootprintTouches. Materials sharing the same diffuse (a body split
    /// across torso/legs materials) are already one editable canvas via
    /// ModelUvReader.GetBodyMesh and are skipped here to avoid a redundant companion of itself.
    /// One source of truth: the original layer, still owned by its own texture — no separate
    /// decal layers to keep in sync when the user edits/moves it.
    /// </summary>
    public static List<OverlayCompanionTarget> OverlayCompanionTargets(DTextureData data, ShaderHandlerRegistry handlers,
        SourceFileProvider files, ModelUvReader uvReader)
    {
        var targets = new List<OverlayCompanionTarget>();

        var bodyFamily = new List<(SourcePath Source, string Diffuse, List<DecalLayer> SurfaceLayers)>();
        foreach (var source in data.Source.Materials)
        {
            if (!ModelUvReader.IsBodySkinMaterial(source.GamePath))
                continue;

            var mtrl = files.GetMaterial(source, null);
            if (mtrl == null)
                continue;

            var diffuse = handlers.For(mtrl).ClassifyTextures(mtrl).FirstOrDefault(t => t.Slot is TextureSlot.Diffuse).GamePath;
            if (diffuse == null)
                continue;

            var layers = data.Textures.GetValueOrDefault(diffuse)?.OfType<DecalLayer>()
                    .Where(l => l is { Enabled: true, Surface: true }).ToList()
             ?? [];
            bodyFamily.Add((source, diffuse, layers));
        }

        if (bodyFamily.Count < 2)
            return targets; // nothing else in the body family to continue onto

        foreach (var (targetSource, targetDiffuse, _) in bodyFamily)
        {
            var mesh = uvReader.GetMesh(targetSource);
            if (mesh == null)
                continue;

            var touching = new List<TextureLayer>();
            foreach (var (otherSource, otherDiffuse, otherLayers) in bodyFamily)
            {
                if (ReferenceEquals(otherSource, targetSource) || string.Equals(otherDiffuse, targetDiffuse, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var layer in otherLayers)
                    if (SurfaceDecalBaker.FootprintTouches(mesh, layer))
                        touching.Add(layer);
            }

            if (touching.Count > 0)
                targets.Add(new OverlayCompanionTarget(targetDiffuse, touching, targetSource));
        }

        return targets;
    }
}
