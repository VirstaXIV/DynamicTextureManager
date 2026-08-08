using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.Events;
using DynamicTextureManager.Interop;
using DynamicTextureManager.Services;
using IService = Luna.IService;
using Penumbra.Api.Enums;
using Penumbra.GameData.Files.MaterialStructs;

namespace DynamicTextureManager.ModGeneration;

// The animated-highlight build: stages the characterscroll replacement material and
// derives its four companion textures from the composited hair normal/mask.
public sealed partial class OverlayModManager
{
    /// <summary>
    /// One animated-highlight conversion: after the material's composited hair NORMAL and
    /// MASK are produced (their texture jobs run in the same build), the four companion
    /// textures of the characterscroll replacement material are derived from them and
    /// written. MaskGamePath is empty when the source material has no mask — the flat
    /// reference tile ships instead.
    /// </summary>
    /// <param name="FullCoverage">
    /// The source normal carries no highlight-blend channel (tails) — the effect covers the
    /// whole piece instead of following highlight areas; see AnimatedHairBuilder.
    /// </param>
    private sealed record AnimatedHairJob(string MaterialGamePath, string NormalGamePath, string MaskGamePath,
        AnimatedHairBuilder.TexturePaths Paths, DTextures.Data.AnimatedHairEdit Edit, bool FullCoverage);

    /// <summary>
    /// Stage every enabled animated-highlight conversion: emit the characterscroll
    /// replacement material now (its structure only depends on the edit) and make sure the
    /// hair NORMAL has a texture job this build — the companion textures derive from its
    /// composited result, so all highlight edits still shape where the effect appears.
    /// </summary>
    private List<AnimatedHairJob> PrepareAnimatedHair(DTexture dTexture, string modDirectory,
        Dictionary<string, byte[]> materials, List<TextureJob> textures)
    {
        var animated  = new List<AnimatedHairJob>();
        var converted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void EnsureTextureJob(string gamePath)
        {
            // The composited texture must exist in this build even when it has no layers.
            if (textures.Any(t => string.Equals(t.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)))
                return;

            var layers   = dTexture.Data.Textures.GetValueOrDefault(gamePath) ?? [];
            var diskPath = GetOrCaptureTextureSource(dTexture, gamePath);
            MaterialMesh? mesh = null;
            if (layers.Any(l => l.Enabled && l.NeedsMeshGeometry))
            {
                var owner = CompositePlanner.FindTextureOwner(dTexture.Data, gamePath, shaderHandlers, sourceFiles);
                mesh = owner != null ? uvReader.GetMesh(owner) : null;
            }

            textures.Add(new TextureJob(gamePath, diskPath is { Length: > 0 } ? diskPath : null, layers, mesh));
        }

        bool Convert(string gamePath, Penumbra.GameData.Files.MtrlFile mtrl, DTextures.Data.AnimatedHairEdit edit)
        {
            var classified = shaderHandlers.For(mtrl).ClassifyTextures(mtrl).ToList();
            var normalPath = classified.FirstOrDefault(t => t.Slot == Shaders.TextureSlot.Normal).GamePath;
            if (string.IsNullOrEmpty(normalPath))
            {
                DynamicTextureManager.Log.Warning($"Animated hair for {gamePath} skipped — no normal texture on the material.");
                return false;
            }

            // The companion mask derives from the hair's own mask so per-strand shading
            // survives the conversion (shine edits included — they layer onto this texture).
            var maskPath = classified.FirstOrDefault(t => t.Slot == Shaders.TextureSlot.Mask).GamePath ?? string.Empty;

            // Tails carry no highlight-blend channel (normal B flat zero — verified on the
            // vanilla Miqo'te tails), so there is no area for the effect to follow: switch
            // to full-piece coverage with the base color kept as the effect row's diffuse.
            // Decided from the SOURCE normal — nothing edits B anymore, so the composited
            // normal the id derives from agrees by construction.
            var normalSource = GetOrCaptureTextureSource(dTexture, normalPath);
            var decodedNormal = textureIO.Load(normalPath, normalSource is { Length: > 0 } ? normalSource : null, modDirectory);
            var fullCoverage  = decodedNormal != null && AnimatedHairBuilder.IsFlatHighlightChannel(decodedNormal.Rgba);
            if (fullCoverage)
                DynamicTextureManager.Log.Information(
                    $"Animated effect for {gamePath}: no highlight channel in the normal — using full-piece coverage.");

            var paths = AnimatedHairBuilder.PathsFor(gamePath);
            materials[gamePath] = AnimatedHairBuilder.BuildMaterial(mtrl, edit, paths, fullCoverage);
            EnsureTextureJob(normalPath);
            if (maskPath.Length > 0)
                EnsureTextureJob(maskPath);

            animated.Add(new AnimatedHairJob(gamePath, normalPath, maskPath, paths, edit, fullCoverage));
            converted.Add(gamePath);
            return true;
        }

        foreach (var (gamePath, storedEdit) in dTexture.Data.AnimatedHair.Where(kvp => kvp.Value.Enabled))
        {
            if (converted.Contains(gamePath))
                continue;

            // Hair + highlight colors follow the CHARACTER (Glamourer included) unless the
            // override toggle is set: resolve the live colors into the baked copy at build
            // time, squared to the colorset domain. Unreadable character -> the stored
            // fallback bakes instead. The effect color is always the stored one.
            var edit = storedEdit;
            if (!edit.OverrideHairColors)
            {
                if (hairColors.TryGetLocalPlayerHair(out var live))
                {
                    edit           = edit.Clone();
                    edit.BaseColor = [live.Main.X * live.Main.X, live.Main.Y * live.Main.Y, live.Main.Z * live.Main.Z];
                    edit.HighlightColor =
                    [
                        live.Highlight.X * live.Highlight.X, live.Highlight.Y * live.Highlight.Y,
                        live.Highlight.Z * live.Highlight.Z,
                    ];
                }
                else
                {
                    DynamicTextureManager.Log.Warning(
                        "Animated hair: character colors unreadable — baking the stored fallback colors.");
                }
            }

            var source = dTexture.Data.Source.Materials.FirstOrDefault(m
                => string.Equals(m.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                DynamicTextureManager.Log.Warning($"Animated hair for {gamePath} skipped — material is not part of the source.");
                continue;
            }

            var mtrl = sourceFiles.GetMaterial(source, modDirectory);
            if (mtrl == null || !Convert(gamePath, mtrl, edit))
                continue;

            // Multi-material hairstyles: the MODEL references every material of the style —
            // convert them all, whether or not each was ever added as a source. A partial
            // conversion leaves whole meshes on the plain hair shader.
            foreach (var rawName in uvReader.ModelMaterialNames(source))
            {
                var fileName    = Path.GetFileName(rawName);
                var siblingPath = AnimatedHairBuilder.SiblingMaterialGamePath(gamePath, fileName);
                if (siblingPath == null || converted.Contains(siblingPath))
                    continue;

                var siblingMtrl = sourceFiles.GetMaterial(new DTextures.Data.SourcePath { GamePath = siblingPath }, modDirectory);
                if (siblingMtrl == null)
                {
                    DynamicTextureManager.Log.Warning($"Animated hair sibling {siblingPath} could not be loaded, skipped.");
                    continue;
                }

                if (shaderHandlers.For(siblingMtrl).Kind(siblingMtrl) is not Shaders.MaterialKind.Hair)
                {
                    DynamicTextureManager.Log.Debug(
                        $"Model material {fileName} is not a hair-shader material — left unconverted.");
                    continue;
                }

                if (Convert(siblingPath, siblingMtrl, edit))
                    DynamicTextureManager.Log.Information(
                        $"Animated hair: also converting hairstyle sibling {siblingPath} (referenced by {source.MdlGamePath}).");
            }
        }

        return animated;
    }

    /// <summary>
    /// The black/white pattern the effect scrolls: a user-picked custom image, the game's own
    /// sparkle texture (loaded from the player's files — never shipped with the plugin), or
    /// the selected built-in pattern (the material references the effect texture
    /// unconditionally, so something always ships).
    /// </summary>
    private (byte[] Rgba, int Width) LoadEffectImage(DTextures.Data.AnimatedHairEdit edit)
    {
        if (edit.EffectLibraryId != Guid.Empty)
        {
            var file = decals.EffectFilePath(edit.EffectLibraryId);
            if (File.Exists(file))
                try
                {
                    using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(file);
                    var pixels = new byte[image.Width * image.Height * 4];
                    image.CopyPixelDataTo(pixels);
                    return (pixels, image.Width);
                }
                catch (Exception ex)
                {
                    DynamicTextureManager.Log.Warning($"Could not load library effect pattern {edit.EffectLibraryId} ({ex.Message}) — using the built-in pattern.");
                }
            else
                DynamicTextureManager.Log.Warning($"Effect pattern {edit.EffectLibraryId} is missing from the library — using the built-in pattern.");
        }

        if ((AnimatedHairBuilder.HairEffectPattern)edit.Pattern is AnimatedHairBuilder.HairEffectPattern.DressGlitter)
        {
            var glitter = textureIO.Load(AnimatedHairBuilder.DressGlitterTexPath, null, null);
            if (glitter != null)
                return (glitter.Rgba, glitter.Width);

            DynamicTextureManager.Log.Warning("Could not load the game's glitter texture — using the built-in Shimmer pattern.");
        }

        var pattern = (AnimatedHairBuilder.HairEffectPattern)edit.Pattern;
        if (pattern is AnimatedHairBuilder.HairEffectPattern.DressGlitter)
            pattern = AnimatedHairBuilder.HairEffectPattern.Shimmer;
        var dimension = AnimatedHairBuilder.PatternDimension(pattern);
        return (AnimatedHairBuilder.GeneratePattern(pattern, dimension), dimension);
    }

}
