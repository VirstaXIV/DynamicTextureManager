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
            => Layers.Any(l => l.NeedsMeshGeometry);
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
            var effectLayers = layers
                .Where(l => l.Enabled && l.HasSiblingEffects)
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
                    .Where(l => info.Slot == TextureSlot.Normal ? l.WantsNormalEffect : l.WantsMaskEffect)
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

        AddBodyFamilyReliefTargets(data, handlers, files, targets);
        return targets;
    }

    /// <summary>
    /// Procedural surface layers cover every body-family canvas (see
    /// <see cref="OverlayCompanionTargets"/>), so their relief/finish must also reach the
    /// OTHER family members' normal/mask textures — baked on that member's own mesh (the
    /// target's Owner), or the fur stands on the body while the face stays flat.
    /// </summary>
    private static void AddBodyFamilyReliefTargets(DTextureData data, ShaderHandlerRegistry handlers,
        SourceFileProvider files, List<SiblingEffectTarget> targets)
    {
        var procedural = new List<(SourcePath Owner, List<TextureLayer> Layers)>();
        foreach (var (gamePath, layers) in data.Textures)
        {
            var procLayers = layers.Where(l => l is ProceduralSurfaceLayer && l.Enabled && l.HasSiblingEffects).ToList();
            if (procLayers.Count == 0)
                continue;

            var owner = FindTextureOwner(data, gamePath, handlers, files);
            if (owner == null || !IsBodyFamilySkinMaterial(owner.GamePath))
                continue;

            procedural.Add((owner, procLayers));
        }

        if (procedural.Count == 0)
            return;

        foreach (var source in data.Source.Materials)
        {
            if (!IsBodyFamilySkinMaterial(source.GamePath))
                continue;

            var mtrl = files.GetMaterial(source, null);
            if (mtrl == null)
                continue;

            foreach (var info in handlers.For(mtrl).ClassifyTextures(mtrl))
            {
                if (info.Slot is not (TextureSlot.Normal or TextureSlot.Mask))
                    continue;

                var slotLayers = procedural
                    .Where(p => !string.Equals(p.Owner.GamePath, source.GamePath, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(p => p.Layers)
                    .Where(l => info.Slot == TextureSlot.Normal ? l.WantsNormalEffect : l.WantsMaskEffect)
                    .ToList();
                if (slotLayers.Count == 0)
                    continue;

                // Body split materials share normal/mask paths with the owner's own sibling
                // targets — never double-add the same layer instance to one target.
                var existing = targets.FindIndex(t => string.Equals(t.GamePath, info.GamePath, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                {
                    foreach (var layer in slotLayers.Where(l => !targets[existing].Layers.Contains(l)))
                        targets[existing].Layers.Add(layer);
                }
                else
                {
                    targets.Add(new SiblingEffectTarget(info.GamePath, info.Slot, slotLayers, source));
                }
            }
        }
    }

    /// <summary>
    /// A body-overlay texture (nails, accents — added as its own source material, see
    /// ModelUvReader.GetBodyOverlayMaterials) whose mesh an enabled body-skin surface decal's
    /// footprint overlaps: that decal's layer(s) should also bake onto this texture, so a
    /// tattoo continues seamlessly across the seam.
    /// </summary>
    public sealed record OverlayCompanionTarget(string GamePath, List<TextureLayer> Layers, SourcePath Owner);

    /// <summary>
    /// The skin family that forms one continuous surface on a character: the body canvas,
    /// its overlay parts (nails/accents — body-pathed materials), and the face. Full-coverage
    /// patterns and overlapping tattoos continue across all of them.
    /// </summary>
    public static bool IsBodyFamilySkinMaterial(string materialGamePath)
        => ModelUvReader.IsBodySkinMaterial(materialGamePath) || ModelUvReader.IsFaceSkinMaterial(materialGamePath);

    /// <summary>
    /// Every pair of body-family source materials (the body itself, added overlay parts, the
    /// face) where one's enabled surface layers reach the other's own mesh: a surface decal
    /// when its footprint touches it (SurfaceDecalBaker.FootprintTouches), a procedural
    /// surface layer always (full coverage — evaluated in world space, so it continues
    /// seamlessly on the companion mesh). Materials sharing the same diffuse (a body split
    /// across torso/legs materials) are already one editable canvas via
    /// ModelUvReader.GetBodyMesh and are skipped here to avoid a redundant companion of itself.
    /// One source of truth: the original layer, still owned by its own texture — no separate
    /// layers to keep in sync when the user edits/moves it.
    /// </summary>
    public static List<OverlayCompanionTarget> OverlayCompanionTargets(DTextureData data, ShaderHandlerRegistry handlers,
        SourceFileProvider files, ModelUvReader uvReader)
    {
        var targets = new List<OverlayCompanionTarget>();

        var bodyFamily = new List<(SourcePath Source, string Diffuse, List<TextureLayer> SurfaceLayers)>();
        foreach (var source in data.Source.Materials)
        {
            if (!IsBodyFamilySkinMaterial(source.GamePath))
                continue;

            var mtrl = files.GetMaterial(source, null);
            if (mtrl == null)
                continue;

            var diffuse = handlers.For(mtrl).ClassifyTextures(mtrl).FirstOrDefault(t => t.Slot is TextureSlot.Diffuse).GamePath;
            if (diffuse == null)
                continue;

            var layers = data.Textures.GetValueOrDefault(diffuse)?
                    .Where(l => l.Enabled && l is DecalLayer { Surface: true } or ProceduralSurfaceLayer).ToList()
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
                    if (layer is ProceduralSurfaceLayer
                     || (layer is DecalLayer decal && SurfaceDecalBaker.FootprintTouches(mesh, decal)))
                        touching.Add(layer);
            }

            if (touching.Count > 0)
                targets.Add(new OverlayCompanionTarget(targetDiffuse, touching, targetSource));
        }

        return targets;
    }
}
