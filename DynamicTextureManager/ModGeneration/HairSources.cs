using System;
using System.IO;
using System.Linq;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Interop;
using DynamicTextureManager.ModGeneration.Shaders;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// A hairstyle is presented as ONE source even when its model splits the strands across
/// several materials: the user picks the primary (scalp) material, and the model's other
/// hair materials ride along as hidden companion sources (Overlay = true — visible in the
/// Textures tab and the 3D preview, absent from the material selector). Discovery reads the
/// model's own material list, the same mechanism the animated-highlight build uses.
/// </summary>
public static class HairSources
{
    /// <summary>
    /// Add the hidden companion sources for a hairstyle's primary material. Idempotent;
    /// returns how many companions were newly added.
    /// </summary>
    public static int AddCompanions(DTextureData data, SourcePath primary, ModelUvReader uvReader,
        SourceFileProvider sourceFiles, ShaderHandlerRegistry shaderHandlers, PenumbraService penumbra)
    {
        var added = 0;
        foreach (var raw in uvReader.ModelMaterialNames(primary))
        {
            var fileName    = Path.GetFileName(raw);
            var siblingPath = AnimatedHairBuilder.SiblingMaterialGamePath(primary.GamePath, fileName);
            if (siblingPath == null
             || data.Source.Materials.Any(m => string.Equals(m.GamePath, siblingPath, StringComparison.OrdinalIgnoreCase)))
                continue;

            var mtrl = sourceFiles.GetMaterial(new SourcePath { GamePath = siblingPath }, null);
            if (mtrl == null || shaderHandlers.For(mtrl).Kind(mtrl) is not MaterialKind.Hair)
                continue;

            var actual = penumbra.Available ? penumbra.ResolvePlayerPath(siblingPath) : string.Empty;
            var mod    = Path.IsPathRooted(actual) ? penumbra.IdentifyModOfFile(actual) : null;
            data.Source.Materials.Add(new SourcePath
            {
                GamePath      = siblingPath,
                ActualPath    = Path.IsPathRooted(actual) ? actual : string.Empty,
                Label         = $"{primary.Label} (part)",
                ModDirectory  = mod?.ModDirectory ?? string.Empty,
                ModName       = mod?.ModName ?? string.Empty,
                MdlGamePath   = primary.MdlGamePath,
                MdlActualPath = primary.MdlActualPath,
                Overlay       = true,
            });
            ++added;
            DynamicTextureManager.Log.Information($"Added hairstyle companion material {siblingPath}.");
        }

        return added;
    }
}
