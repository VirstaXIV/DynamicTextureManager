using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration.Shaders;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Bakes a procedural surface layer (fur, scales, skin patterns) over the whole editable mesh
/// surface. The pattern is evaluated in world space at each texel's surface position, so it is
/// seamless across UV islands and material splits; a guide-anchor flow field orients it along
/// the body. Three stages: rasterize every accepted triangle into per-texel surface fields,
/// evaluate the generator into height/albedo/coverage planes, then compose the requested
/// output (diffuse colors, or relief/finish for sibling textures). The generated planes are a
/// pure function of (layer content, mesh, resolution) and are cached — the diffuse bake and
/// the sibling replays share one generation, and repeated composites are lookups.
/// </summary>
public static partial class ProceduralSurfaceBaker
{
    /// <param name="effectSlot">
    /// When set, the bake targets a sibling texture of the same material (normal/mask):
    /// the footprint is identical, but each texel receives the layer's relief or finish
    /// instead of its colors.
    /// </param>
    public static void Bake(Image<Rgba32> target, MaterialMesh mesh, ProceduralSurfaceLayer layer,
        TextureSlot? effectSlot = null, CharacterColors characterColors = default,
        MarkingPatternImage? markingPattern = null)
    {
        if (layer.Opacity <= 0f)
            return;

        var generated = GetOrGenerate(mesh, layer, target.Width, target.Height, characterColors, markingPattern);
        if (generated == null)
            return;

        switch (effectSlot)
        {
            case null:
                ComposeDiffuse(target, generated, layer);
                break;
            case TextureSlot.Normal when layer.WantsNormalEffect:
                ComposeNormal(target, generated, layer);
                break;
            case TextureSlot.Mask when layer.WantsMaskEffect || FinishMapping.ProceduralMaskWriteCavity:
                ComposeMask(target, generated, layer);
                break;
        }
    }

    // ------------------------------------------------------------------ generation cache

    /// <summary>
    /// The generator's output planes at one resolution. Row-major parallel arrays, kept
    /// compact on purpose — a 4K body texture is 16.7M texels and these live in the cache:
    /// coverage (pattern presence × exclusion weight) quantized to a byte, height to 16 bits,
    /// color packed. A zero coverage byte doubles as "texel not covered".
    /// </summary>
    private sealed class GeneratedFields
    {
        public required byte[]   Coverage;
        public required ushort[] Height;
        public required uint[]   Albedo;
        public required float[]  TexelsPerMeter;
    }

    // One generation is a pure function of (layer content, skin tone, mesh, W, H). Keyed per
    // mesh so entries die with the mesh; two entries absorb the common diffuse resolution
    // plus a differently-sized normal/mask sibling without thrashing during slider edits.
    private const int CachePerMesh = 2;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MaterialMesh,
        Dictionary<string, (GeneratedFields Fields, long Seq)>> Cache = new();

    private static long _cacheSeq;

    private static GeneratedFields? GetOrGenerate(MaterialMesh mesh, ProceduralSurfaceLayer layer,
        int width, int height, CharacterColors characterColors, MarkingPatternImage? markingPattern)
    {
        static string Tone(Vector3? v)
            => v is { } t ? FormattableString.Invariant($"{t.X:F3},{t.Y:F3},{t.Z:F3}") : "-";

        var tones = layer.UseCharacterColors
            ? $"{Tone(characterColors.HairMain)}|{Tone(characterColors.HairHighlight)}"
            : "none";
        var key   = $"{layer.ContentHash()}|{width}x{height}|{tones}";
        if (markingPattern != null)
            key += $"|mp{markingPattern.Stamp}";
        var table = Cache.GetOrCreateValue(mesh);

        lock (table)
        {
            if (table.TryGetValue(key, out var hit))
            {
                table[key] = (hit.Fields, ++_cacheSeq);
                return hit.Fields;
            }
        }

        var fields = Generate(mesh, layer, width, height, characterColors, markingPattern);
        if (fields == null)
            return null;

        lock (table)
        {
            while (table.Count >= CachePerMesh)
            {
                string? oldest = null;
                var oldestSeq  = long.MaxValue;
                foreach (var (k, v) in table)
                    if (v.Seq < oldestSeq)
                    {
                        oldestSeq = v.Seq;
                        oldest    = k;
                    }

                if (oldest == null)
                    break;

                table.Remove(oldest);
            }

            table[key] = (fields, ++_cacheSeq);
        }

        return fields;
    }
}
