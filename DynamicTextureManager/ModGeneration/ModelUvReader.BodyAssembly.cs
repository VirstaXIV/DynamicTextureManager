using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Interop;
using IService = Luna.IService;
using Penumbra.GameData.Files;

namespace DynamicTextureManager.ModGeneration;

// Body canvas assembly: resolve the SmallClothes model set through Penumbra, read it,
// decide which of its materials form the editable body canvas, and surface the rest
// as separate overlay-part canvases (nails, claws, accents).
public sealed partial class ModelUvReader
{
    /// <summary>
    /// Material file names the resolved SmallClothes body models reference — i.e. the skin
    /// materials the character's body actually renders with — each mapped to a bitmask of the
    /// SmallClothes slots referencing it (bit 0 top, 1 legs, 2 hands, 3 feet). Torso/legs bits
    /// mark THE body canvas; a hands/feet-only material is a part (feet replacements, claws,
    /// nails). A body material outside this set (e.g. the vanilla _a material while a body mod
    /// is active) only shows on stray gear-embedded patches, so decals on it are effectively
    /// invisible.
    /// </summary>
    public Dictionary<string, int> ResolvedBodyMaterialSlots(string race)
    {
        var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var set   = BodyModelSetForRace(race);
        for (var i = 0; i < set.Length; ++i)
        {
            try
            {
                var actual = penumbra.ResolvePlayerPath(set[i]);
                var bytes = actual.Length > 0 && Path.IsPathRooted(actual) && File.Exists(actual)
                    ? File.ReadAllBytes(actual)
                    : dataManager.GetFile(set[i])?.Data;
                if (bytes == null)
                    continue;

                // Model material names carry their authoring race — normalize to the wearer's
                // race, exactly like the game's load-time substitution.
                foreach (var material in new MdlFile(bytes).Materials)
                {
                    var name = SubstituteBodyRace(Path.GetFileName(material), race);
                    slots[name] = slots.GetValueOrDefault(name) | (1 << i);
                }
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Warning($"Could not read materials of body model {set[i]}: {ex.Message}");
            }
        }

        return slots;
    }

    /// <summary>
    /// Material file names the UNMODDED SmallClothes body models reference (race-substituted)
    /// — the vanilla body material family. A tree material in this set while the resolved
    /// body renders with something else is the vanilla-compat set bibo-family mods override
    /// for gear-embedded skin patches: the SAME body under an alternate material.
    /// </summary>
    public HashSet<string> VanillaBodyMaterialNames(string race)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gamePath in BodyModelSetForRace(race))
        {
            try
            {
                var bytes = dataManager.GetFile(gamePath)?.Data;
                if (bytes == null)
                    continue;

                foreach (var material in new MdlFile(bytes).Materials)
                    names.Add(SubstituteBodyRace(Path.GetFileName(material), race));
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Warning($"Could not read materials of vanilla body model {gamePath}: {ex.Message}");
            }
        }

        return names;
    }

    /// <summary>
    /// The whole SmallClothes body as one mesh: every mesh of all four models concatenated,
    /// with the source material's meshes editable and everything else as dimmed context —
    /// the body always shows as one unit even when a body mod splits it across materials.
    /// Each model resolves through Penumbra so modded bodies load. Null (fall back to the
    /// recorded model) when nothing references the material — e.g. nonstandard NPC bodies.
    /// </summary>
    /// <summary>
    /// Resolve the SmallClothes model set through Penumbra for the cache key. The UI asks for
    /// body meshes every frame (viewport, overlay entries), and each resolve is four IPC
    /// round trips — a short TTL keeps mod switches visible while dropping the per-frame cost.
    /// </summary>
    private (string Race, (string GamePath, string Actual)[] Resolved, string Joined) ResolveBodyModels(SourcePath source)
    {
        var topMatch = BodyTopModelPattern.Match(source.MdlGamePath);
        var race     = topMatch.Success ? topMatch.Groups[1].Value : BodyMaterialRace(source.GamePath);
        var now      = Environment.TickCount64;
        if (_bodyResolveCache.TryGetValue(race, out var hit) && now - hit.AtMs < BodyResolveTtlMs)
            return (race, hit.Resolved, hit.Joined);

        var resolved = Array.ConvertAll(BodyModelSetForRace(race), p => (p, penumbra.ResolvePlayerPath(p)));
        var joined   = string.Join(";", resolved.Select(r => r.Item2));
        _bodyResolveCache[race] = (now, resolved, joined);
        return (race, resolved, joined);
    }

    private const long BodyResolveTtlMs = 1000;

    private readonly Dictionary<string, (long AtMs, (string GamePath, string Actual)[] Resolved, string Joined)> _bodyResolveCache = [];

    /// <summary>
    /// Read and parse the resolved model set — only on cache misses. Units keep the
    /// SmallClothes slot index (0 top, 1 legs, 2 hands, 3 feet) even when a model fails
    /// to load, so per-part weights stay stable.
    /// </summary>
    private (List<MdlFile> Models, List<byte> Units) LoadBodyModels((string GamePath, string Actual)[] resolved)
    {
        var models = new List<MdlFile>();
        var units  = new List<byte>();
        for (var i = 0; i < resolved.Length; ++i)
        {
            var (gamePath, actual) = resolved[i];
            var bytes = actual.Length > 0 && Path.IsPathRooted(actual) && File.Exists(actual)
                ? File.ReadAllBytes(actual)
                : dataManager.GetFile(gamePath)?.Data;
            if (bytes != null)
            {
                models.Add(new MdlFile(bytes));
                units.Add((byte)i);
            }
            else
            {
                DynamicTextureManager.Log.Warning($"Could not load body model {gamePath} (file \"{actual}\").");
            }
        }

        return (models, units);
    }

    private MaterialMesh? GetBodyMesh(SourcePath source)
    {
        var (race, resolved, joined) = ResolveBodyModels(source);
        var key = $"bodyset|{source.GamePath}|{joined}";
        if (_meshCache.TryGetValue(key, out var cached))
            return cached;

        MaterialMesh? mesh = null;
        try
        {
            var (models, units) = LoadBodyModels(resolved);
            var (sourceName, _, editableNames) = ComputeEditableBodyMaterials(source, race, models);

            // The merged mesh's GamePath is deliberately the material path: it must not look
            // like an equipment model, or the live attribute-mask lookup would apply the worn
            // gear's variant mask to the nude body.
            mesh = MdlMeshReader.ReadMeshes(models,
                material => editableNames.Contains(SubstituteBodyRace(Path.GetFileName(material), race)), sourceName,
                source.GamePath, includeContext: true, modelUnits: units);

            // Vanilla-compat set: gear-embedded skin patches render with the VANILLA body
            // material, and bibo-family mods override its textures so gear matches (Muse's
            // _a) — worn false-nail gloves render their hand skin with it. The resolved
            // (modded) body models never reference it, but the UNMODDED SmallClothes set
            // does, and every gear skin patch maps into that layout: bake through the
            // vanilla set and any worn piece matches.
            if (mesh == null)
            {
                var vanilla = LoadBodyModels(Array.ConvertAll(BodyModelSetForRace(race), p => (p, string.Empty)));
                var (vanillaName, _, vanillaEditable) = ComputeEditableBodyMaterials(source, race, vanilla.Models);
                mesh = MdlMeshReader.ReadMeshes(vanilla.Models,
                    material => vanillaEditable.Contains(SubstituteBodyRace(Path.GetFileName(material), race)), vanillaName,
                    source.GamePath, includeContext: true, modelUnits: vanilla.Units);
                if (mesh != null)
                    DynamicTextureManager.Log.Information(
                        $"Body geometry of {source.GamePath} comes from the VANILLA SmallClothes set ({race}) — "
                      + "no resolved body model references it (vanilla-compat material).");
            }

            if (mesh != null)
                DynamicTextureManager.Log.Information(
                    $"Body geometry of {source.GamePath}: {mesh.VertexCount} vertices, {mesh.TriangleCount} triangles "
                  + $"({mesh.TriangleEditable.Count(e => e)} editable via [{string.Join(", ", editableNames)}]), "
                  + $"{mesh.PartCount} parts from {models.Count} SmallClothes models ({race}).");
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not read body geometry for {source.GamePath}: {ex.Message}");
        }

        _meshCache[key] = mesh;
        return mesh;
    }

    /// <summary>
    /// Which materials of the resolved SmallClothes model set count as the SAME editable canvas
    /// as <paramref name="source"/>: the source material itself (after race substitution —
    /// model material names carry their authoring race, resolved to the wearer's race at load),
    /// plus every material painting the SAME diffuse texture. Body mods split the body into
    /// several materials sharing one full-body texture — a decal must continue across those
    /// seams, so the whole shared canvas is editable. Shared by <see cref="GetBodyMesh"/> (which
    /// paints only these) and <see cref="GetBodyOverlayMaterials"/> (which offers everything
    /// else in the set as separate, addable overlay-part canvases).
    /// </summary>
    private (string SourceName, string Variant, HashSet<string> EditableNames) ComputeEditableBodyMaterials(
        SourcePath source, string race, List<MdlFile> models)
    {
        var sourceName    = SubstituteBodyRace(Path.GetFileName(source.GamePath), race);
        var variant       = MaterialVariantPattern.Match(source.GamePath) is { Success: true } vm ? vm.Groups[1].Value : "v0001";
        var sourceDiffuse = MaterialDiffusePath(source.GamePath, source.ActualPath);
        var editableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceName };
        if (sourceDiffuse.Length > 0)
            foreach (var raw in models.SelectMany(m => m.Materials).Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var name = SubstituteBodyRace(raw!, race);
                if (editableNames.Contains(name))
                    continue;

                var gamePath = BodyMaterialGamePath(name, variant);
                if (gamePath != null && string.Equals(MaterialDiffusePath(gamePath), sourceDiffuse, StringComparison.OrdinalIgnoreCase))
                    editableNames.Add(name);
            }

        return (sourceName, variant, editableNames);
    }

    /// <summary>
    /// A body-context material with its own diffuse texture — a candidate for a body tattoo to
    /// continue onto (nails, claws, accents). Colorset-only pieces (piercings) and hair-shader
    /// pieces (pubic hair) have no diffuse sampler and are excluded: decals cannot paint them
    /// via the diffuse-bake mechanism this offers.
    /// </summary>
    public sealed record BodyOverlayMaterial(string Name, string GamePath, string DiffusePath);

    /// <summary>
    /// Diffuse-paintable overlay-part materials referenced by the same 4 SmallClothes models as
    /// the body skin canvas, but NOT part of it — see <see cref="ComputeEditableBodyMaterials"/>
    /// for what counts as "part of it". Discovered 2026-07 via forensic logging: body mods like
    /// bibo embed nail/claw/accent geometry directly in the SmallClothes models under their own
    /// materials (e.g. "mt_c0201b0001_trenails.mtrl", diffuse "chara/common/texture/
    /// mewnails_base.tex") — previously invisible to source selection, rendered only as
    /// unpaintable dimmed context in the viewport.
    /// </summary>
    public List<BodyOverlayMaterial> GetBodyOverlayMaterials(SourcePath source)
    {
        var result = new List<BodyOverlayMaterial>();
        try
        {
            var (race, resolved, _) = ResolveBodyModels(source);
            var (models, _) = LoadBodyModels(resolved);
            var (_, variant, editableNames) = ComputeEditableBodyMaterials(source, race, models);

            foreach (var name in models.SelectMany(m => m.Materials).Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase)
                         .Select(n => SubstituteBodyRace(n!, race)).Where(n => !editableNames.Contains(n)))
            {
                var gamePath = BodyMaterialGamePath(name, variant);
                if (gamePath == null)
                    continue;

                var diffuse = MaterialDiffusePath(gamePath);
                if (diffuse.Length > 0)
                    result.Add(new BodyOverlayMaterial(name, gamePath, diffuse));
            }
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not enumerate body overlay materials for {source.GamePath}: {ex.Message}");
        }

        return result;
    }
}
