using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Interop;
using OtterGui.Services;
using Penumbra.GameData.Files;
using Penumbra.GameData.Files.MaterialStructs;

namespace DynamicTextureManager.ModGeneration;

/// <summary> The UV layout of the meshes using one material: its island boundary (seam) edges. </summary>
public sealed class UvLayout
{
    public required IReadOnlyList<(Vector2 A, Vector2 B)> Seams { get; init; }
    public required int TriangleCount { get; init; }
}

/// <summary>
/// Bind-pose geometry of all meshes using one material, concatenated: positions and normals
/// in model space, UVs and a triangle index list — everything surface decals need to pick
/// against in the 3D viewport and to bake with.
/// </summary>
public sealed class MaterialMesh
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] Uvs { get; init; }
    public required int[] Indices { get; init; }

    /// <summary> Per-triangle submesh attribute mask — used to skip variant geometry the game currently hides. </summary>
    public required uint[] TriangleAttributeMasks { get; init; }

    /// <summary> Per-triangle connected-part id: separate mesh pieces (lining, straps, panels) get distinct ids. </summary>
    public required int[] TriangleParts { get; init; }

    /// <summary>
    /// Whether a triangle belongs to the source material (true) or is context geometry from
    /// another material of the same model set (false) — shown dimmed for orientation, but its
    /// UVs live in a different texture, so it is never picked, seeded or baked onto.
    /// </summary>
    public required bool[] TriangleEditable { get; init; }

    public required int PartCount { get; init; }

    /// <summary>
    /// Shape keys in the model's own order (indices must line up with the game's enabled-
    /// shape mask): index-buffer swaps redirecting triangles to morphed alternate vertices.
    /// Applied at pick time so the ray hits the shaped surface the player actually sees.
    /// </summary>
    public required (string Name, (int IndexPosition, int NewVertex)[] Swaps)[] Shapes { get; init; }

    /// <summary> Game path of the model this mesh came from (drives the equipment-slot lookup for attribute masks). </summary>
    public required string GamePath { get; init; }

    private (int[] Canonical, List<int>[] Neighbors)? _adjacency;

    /// <summary>
    /// Vertex adjacency for surface-following decal projection (<see cref="SurfaceDecalBaker.
    /// ComputeSurfaceProjection"/>): vertices are welded by position (0.1mm, matching
    /// <see cref="ComputeParts"/> in <see cref="ModelUvReader"/>) so a walk crosses UV-seam
    /// vertex duplicates as one continuous surface, then connected along every triangle edge —
    /// including non-editable context triangles, so the walk sees the true connectivity of the
    /// underlying body even though only editable triangles are ever painted. Built lazily once
    /// and cached; reused across every decal placed on this mesh.
    /// </summary>
    public (int[] Canonical, List<int>[] Neighbors) GetOrBuildAdjacency()
    {
        if (_adjacency is { } cached)
            return cached;

        var canonical  = new int[Positions.Length];
        var byPosition = new Dictionary<(long, long, long), int>();
        for (var v = 0; v < Positions.Length; ++v)
        {
            var p   = Positions[v];
            var key = ((long)Math.Round(p.X * 10000), (long)Math.Round(p.Y * 10000), (long)Math.Round(p.Z * 10000));
            if (byPosition.TryGetValue(key, out var first))
                canonical[v] = first;
            else
            {
                byPosition[key] = v;
                canonical[v]    = v;
            }
        }

        var neighborSets = new HashSet<int>?[Positions.Length];

        void Connect(int a, int b)
        {
            if (a == b)
                return;
            (neighborSets[a] ??= []).Add(b);
            (neighborSets[b] ??= []).Add(a);
        }

        for (var i = 0; i + 2 < Indices.Length; i += 3)
        {
            var c0 = canonical[Indices[i]];
            var c1 = canonical[Indices[i + 1]];
            var c2 = canonical[Indices[i + 2]];
            Connect(c0, c1);
            Connect(c1, c2);
            Connect(c2, c0);
        }

        var neighbors = new List<int>[Positions.Length];
        for (var v = 0; v < Positions.Length; ++v)
            neighbors[v] = neighborSets[v] is { } set ? [.. set] : [];

        _adjacency = (canonical, neighbors);
        return _adjacency.Value;
    }

    public int VertexCount
        => Positions.Length;

    public int TriangleCount
        => Indices.Length / 3;

    /// <summary> The index buffer with a set of enabled shape keys applied (the base buffer for mask 0). </summary>
    public int[] IndicesWithShapes(uint shapeMask)
    {
        if (shapeMask == 0 || Shapes.Length == 0)
            return Indices;

        var result = (int[])Indices.Clone();
        for (var s = 0; s < Shapes.Length && s < 32; ++s)
        {
            if ((shapeMask & (1u << s)) == 0)
                continue;

            foreach (var (position, newVertex) in Shapes[s].Swaps)
                if (position < result.Length && newVertex < VertexCount)
                    result[position] = newVertex;
        }

        return result;
    }
}

/// <summary>
/// Reads model geometry for source materials: the UV layout so the texture preview can show
/// where UV islands end, and the full bind-pose mesh for surface-projected decals.
/// </summary>
public sealed class ModelUvReader(IDataManager dataManager, PenumbraService penumbra) : IService
{
    private readonly Dictionary<string, MaterialMesh?> _meshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UvLayout?>     _uvCache   = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Body skin materials live under chara/human but have NO model of their own — the game
    /// has no nude body model. The SmallClothes equipment models (e0000) ARE the nude body;
    /// worn gear models merely embed the skin patches they expose.
    /// </summary>
    private static readonly Regex BodySkinMaterialPattern =
        new(@"^chara/human/(c\d{4})/obj/body/b\d{4}/material/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The recorded model of a body skin source, carrying the race code of the SmallClothes
    /// set to load. NOT derivable from the material path: body-mod families deliberately use
    /// foreign race codes in their material paths (e.g. bibo's c0101-pathed material on a
    /// c0201 female body), so the race must come from the models actually worn.
    /// </summary>
    private static readonly Regex BodyTopModelPattern =
        new(@"^chara/equipment/e0000/model/(c\d{4})e0000_top\.mdl$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsBodySkinMaterial(string materialGamePath)
        => BodySkinMaterialPattern.IsMatch(materialGamePath);

    /// <summary> Body material file name (mt_cXXXXbYYYY_*.mtrl) → its conventional game path; the race/body codes live in the name itself. </summary>
    private static readonly Regex BodyMaterialNamePattern =
        new(@"^mt_(c\d{4})(b\d{4})_", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MaterialVariantPattern =
        new(@"/material/(v\d{4})/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? BodyMaterialGamePath(string materialFileName, string variant)
    {
        var match = BodyMaterialNamePattern.Match(materialFileName);
        return match.Success
            ? $"chara/human/{match.Groups[1].Value}/obj/body/{match.Groups[2].Value}/material/{variant}/{materialFileName}"
            : null;
    }

    /// <summary>
    /// The game substitutes the wearer's race code into body material names referenced by
    /// models — a model authored as c0101 resolves its "/mt_c0101b0001_bibo.mtrl" to
    /// mt_c0201b0001_bibo.mtrl on a c0201 body. Matching must do the same, or the torso of a
    /// mixed-authoring body mod never matches its own material.
    /// </summary>
    private static string SubstituteBodyRace(string materialFileName, string race)
        => BodyMaterialNamePattern.IsMatch(materialFileName) ? $"mt_{race}{materialFileName[8..]}" : materialFileName;

    /// <summary> Resolve and read a game file: recorded actual path, then Penumbra resolution, then vanilla. </summary>
    private byte[]? LoadGameFile(string gamePath, string actualPath = "")
    {
        if (actualPath.Length > 0 && Path.IsPathRooted(actualPath) && File.Exists(actualPath))
            return File.ReadAllBytes(actualPath);

        var resolved = penumbra.ResolvePlayerPath(gamePath);
        if (resolved.Length > 0 && Path.IsPathRooted(resolved) && File.Exists(resolved))
            return File.ReadAllBytes(resolved);

        return dataManager.GetFile(gamePath)?.Data;
    }

    /// <summary> The diffuse texture game path a material paints, empty when unreadable. </summary>
    private string MaterialDiffusePath(string gamePath, string actualPath = "")
    {
        try
        {
            var bytes = LoadGameFile(gamePath, actualPath);
            if (bytes == null)
                return string.Empty;

            var mtrl = new MtrlFile(bytes);
            foreach (var sampler in mtrl.ShaderPackage.Samplers)
                if (sampler.SamplerId == ShpkFile.DiffuseSamplerId && sampler.TextureIndex < mtrl.Textures.Length)
                    return mtrl.Textures[sampler.TextureIndex].Path;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not read material {gamePath}: {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>
    /// TEMPORARY diagnostic (2026-07 overlay-part investigation): a quick summary of a
    /// material's shader package and whether it carries a colorset — distinguishes a diffuse-
    /// paintable overlay (nails/accents) from a colorset-driven one (piercings/pubic hair,
    /// which decals cannot target via the tattoo/diffuse-bake mechanism, only via the
    /// gear-style id-map mechanism). Remove alongside the rest of the A0 diagnostic.
    /// </summary>
    private string ClassifyOverlayMaterial(string gamePath)
    {
        try
        {
            var bytes = LoadGameFile(gamePath);
            if (bytes == null)
                return "unreadable";

            var mtrl = new MtrlFile(bytes);
            var hasColorTable = mtrl.Table is ColorTable or LegacyColorTable;
            return $"shader {mtrl.ShaderPackage.Name}, colorTable={hasColorTable}";
        }
        catch (Exception ex)
        {
            return $"unreadable ({ex.Message})";
        }
    }

    /// <summary> The race code in a body skin material's path — a fallback only, see <see cref="BodyTopModelPattern"/>. </summary>
    public static string BodyMaterialRace(string materialGamePath)
    {
        var match = BodySkinMaterialPattern.Match(materialGamePath);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary> The SmallClothes model set (chest, legs, hands, feet) of a body race code. </summary>
    public static string[] BodyModelSetForRace(string race)
        =>
        [
            $"chara/equipment/e0000/model/{race}e0000_top.mdl",
            $"chara/equipment/e0000/model/{race}e0000_dwn.mdl",
            $"chara/equipment/e0000/model/{race}e0000_glv.mdl",
            $"chara/equipment/e0000/model/{race}e0000_sho.mdl",
        ];

    /// <summary> Full bind-pose geometry for a source material, cached; null when the model cannot be read. </summary>
    public MaterialMesh? GetMesh(SourcePath source)
    {
        // Body skin: load the whole SmallClothes body instead of whatever (gear) model the
        // material was found on — tattoos must be placeable anywhere on the body, and gear
        // models only embed the few skin patches they expose.
        if (IsBodySkinMaterial(source.GamePath) && GetBodyMesh(source) is { } bodyMesh)
            return bodyMesh;

        if (source.MdlGamePath.Length == 0)
            return null;

        var key = CacheKey(source);
        if (_meshCache.TryGetValue(key, out var cached))
            return cached;

        MaterialMesh? mesh = null;
        try
        {
            var bytes = LoadModelBytes(source);
            if (bytes == null)
                DynamicTextureManager.Log.Warning(
                    $"Could not load model {source.MdlGamePath} (file \"{source.MdlActualPath}\") for its geometry.");
            else
            {
                var fileName = Path.GetFileName(source.GamePath);
                mesh = ReadMeshes([new MdlFile(bytes)],
                    material => material.EndsWith(fileName, StringComparison.OrdinalIgnoreCase), fileName, source.MdlGamePath);
            }

            if (mesh != null)
                DynamicTextureManager.Log.Information(
                    $"Geometry of {source.GamePath}: {mesh.VertexCount} vertices, {mesh.TriangleCount} triangles, {mesh.PartCount} parts.");
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not read geometry of {source.MdlGamePath}: {ex.Message}");
        }

        _meshCache[key] = mesh;
        return mesh;
    }

    /// <summary>
    /// Material file names the resolved SmallClothes body models reference — i.e. the skin
    /// materials the character's body actually renders with. A body material outside this set
    /// (e.g. the vanilla _a material while a body mod is active) only shows on stray gear-
    /// embedded patches, so decals on it are effectively invisible.
    /// </summary>
    public HashSet<string> ResolvedBodyMaterialNames(string race)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gamePath in BodyModelSetForRace(race))
        {
            try
            {
                var actual = penumbra.ResolvePlayerPath(gamePath);
                var bytes = actual.Length > 0 && Path.IsPathRooted(actual) && File.Exists(actual)
                    ? File.ReadAllBytes(actual)
                    : dataManager.GetFile(gamePath)?.Data;
                if (bytes == null)
                    continue;

                // Model material names carry their authoring race — normalize to the wearer's
                // race, exactly like the game's load-time substitution.
                foreach (var material in new MdlFile(bytes).Materials)
                    names.Add(SubstituteBodyRace(Path.GetFileName(material), race));
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Warning($"Could not read materials of body model {gamePath}: {ex.Message}");
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
    /// <summary> Resolve the SmallClothes model set through Penumbra — cheap enough to run per lookup for the cache key. </summary>
    private (string Race, (string GamePath, string Actual)[] Resolved) ResolveBodyModels(SourcePath source)
    {
        var topMatch = BodyTopModelPattern.Match(source.MdlGamePath);
        var race     = topMatch.Success ? topMatch.Groups[1].Value : BodyMaterialRace(source.GamePath);
        return (race, Array.ConvertAll(BodyModelSetForRace(race), p => (p, penumbra.ResolvePlayerPath(p))));
    }

    /// <summary> Read and parse the resolved model set — only on cache misses. </summary>
    private List<MdlFile> LoadBodyModels((string GamePath, string Actual)[] resolved)
    {
        var models = new List<MdlFile>();
        foreach (var (gamePath, actual) in resolved)
        {
            var bytes = actual.Length > 0 && Path.IsPathRooted(actual) && File.Exists(actual)
                ? File.ReadAllBytes(actual)
                : dataManager.GetFile(gamePath)?.Data;
            if (bytes != null)
                models.Add(new MdlFile(bytes));
            else
                DynamicTextureManager.Log.Warning($"Could not load body model {gamePath} (file \"{actual}\").");
        }

        return models;
    }

    private MaterialMesh? GetBodyMesh(SourcePath source)
    {
        var (race, resolved) = ResolveBodyModels(source);
        var key = $"bodyset|{source.GamePath}|{string.Join(";", resolved.Select(r => r.Actual))}";
        if (_meshCache.TryGetValue(key, out var cached))
            return cached;

        MaterialMesh? mesh = null;
        try
        {
            var models = LoadBodyModels(resolved);
            var (sourceName, _, editableNames) = ComputeEditableBodyMaterials(source, race, models);

            // The merged mesh's GamePath is deliberately the material path: it must not look
            // like an equipment model, or the live attribute-mask lookup would apply the worn
            // gear's variant mask to the nude body.
            mesh = ReadMeshes(models,
                material => editableNames.Contains(SubstituteBodyRace(Path.GetFileName(material), race)), sourceName,
                source.GamePath, includeContext: true);
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
            var (race, resolved) = ResolveBodyModels(source);
            var models = LoadBodyModels(resolved);
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

    /// <summary> UV layout for a source material, cached; null when the model cannot be read. </summary>
    public UvLayout? Get(SourcePath source)
    {
        var key = CacheKey(source);
        if (_uvCache.TryGetValue(key, out var cached))
            return cached;

        var mesh   = GetMesh(source);
        var layout = mesh == null ? null : BuildLayout(mesh);
        _uvCache[key] = layout;
        return layout;
    }

    private static string CacheKey(SourcePath source)
        => $"{source.MdlActualPath}|{source.MdlGamePath}|{source.GamePath}";

    private byte[]? LoadModelBytes(SourcePath source)
    {
        if (source.MdlActualPath.Length > 0 && Path.IsPathRooted(source.MdlActualPath) && File.Exists(source.MdlActualPath))
            return File.ReadAllBytes(source.MdlActualPath);

        return dataManager.GetFile(source.MdlGamePath)?.Data;
    }

    /// <summary>
    /// Map a UV into the base 0..1 tile. The game samples textures with wrap, and modded
    /// models sometimes place their UV islands in a different tile (e.g. V in -1..0) — raw
    /// values would draw seams and bake decals outside the texture. Exact integer values on
    /// a tile's far edge map to 1 so islands touching that edge stay intact; only triangles
    /// genuinely crossing a tile boundary (rare in gear) cannot be represented after wrapping.
    /// </summary>
    private static Vector2 WrapUv(Vector2 uv)
        => new(WrapCoord(uv.X), WrapCoord(uv.Y));

    private static float WrapCoord(float x)
    {
        var wrapped = x - MathF.Floor(x);
        return wrapped == 0f && x >= 1f ? 1f : wrapped;
    }

    /// <summary>
    /// Extract and concatenate the LOD-0 geometry of all editable meshes across the given
    /// models — <paramref name="isEditableMaterial"/> decides per raw mdl material string.
    /// With <paramref name="includeContext"/>, meshes of other materials are included too,
    /// marked non-editable — dimmed orientation geometry whose UVs belong to a different texture.
    /// </summary>
    private static MaterialMesh? ReadMeshes(IReadOnlyList<MdlFile> models, Func<string, bool> isEditableMaterial,
        string materialLabel, string meshGamePath, bool includeContext = false)
    {
        var positions = new List<Vector3>();
        var normals   = new List<Vector3>();
        var uvs       = new List<Vector2>();
        var indices   = new List<int>();
        var triMasks  = new List<uint>();
        var editable  = new List<bool>();
        var shapes    = new List<(string Name, (int IndexPosition, int NewVertex)[] Swaps)>();
        var editableTriangles = 0;

        foreach (var mdl in models)
        {
            if (!mdl.Valid || mdl.LodCount == 0)
                continue;

            var data = mdl.RemainingData;
            var lod  = mdl.Lods[0];
            // Included meshes of this model, for mapping shape-value positions into the concatenated arrays.
            var included = new List<(uint MeshStartIndex, int OurIndexStart, int IndexCount, int VertexOffset, int VertexCount)>();

            for (var m = lod.MeshIndex; m < lod.MeshIndex + lod.MeshCount && m < mdl.Meshes.Length; ++m)
            {
                var mesh = mdl.Meshes[m];
                if (mesh.MaterialIndex >= mdl.Materials.Length)
                    continue;

                var meshEditable = isEditableMaterial(mdl.Materials[mesh.MaterialIndex]);
                if (!meshEditable && !includeContext)
                    continue;

                if (!TryReadMeshVertices(mdl, data, m, out var vertices))
                    continue;

                var indexBase = (int)(mdl.IndexOffset[0] + mesh.StartIndex * 2);
                if (indexBase < 0 || indexBase + mesh.IndexCount * 2 > data.Length)
                    continue;

                var vertexOffset = positions.Count;
                included.Add((mesh.StartIndex, indices.Count, (int)mesh.IndexCount, vertexOffset, vertices.Length));
                foreach (var vertex in vertices)
                {
                    positions.Add(vertex.Position);
                    normals.Add(vertex.Normal);
                    uvs.Add(WrapUv(vertex.Uv));
                }

                // Submesh attribute masks let the picker skip variant geometry the game hides.
                var subMeshes = new List<(uint Start, uint Count, uint Mask)>();
                for (var s = mesh.SubMeshIndex; s < mesh.SubMeshIndex + mesh.SubMeshCount && s < mdl.SubMeshes.Length; ++s)
                    subMeshes.Add((mdl.SubMeshes[s].IndexOffset, mdl.SubMeshes[s].IndexCount, mdl.SubMeshes[s].AttributeIndexMask));

                for (var i = 0; i + 2 < mesh.IndexCount; i += 3)
                {
                    var a = BitConverter.ToUInt16(data, indexBase + i * 2);
                    var b = BitConverter.ToUInt16(data, indexBase + (i + 1) * 2);
                    var c = BitConverter.ToUInt16(data, indexBase + (i + 2) * 2);
                    if (a >= vertices.Length || b >= vertices.Length || c >= vertices.Length)
                        continue;

                    var globalIndex = mesh.StartIndex + (uint)i;
                    var mask        = 0u;
                    foreach (var subMesh in subMeshes)
                    {
                        if (globalIndex >= subMesh.Start && globalIndex < subMesh.Start + subMesh.Count)
                        {
                            mask = subMesh.Mask;
                            break;
                        }
                    }

                    indices.Add(vertexOffset + a);
                    indices.Add(vertexOffset + b);
                    indices.Add(vertexOffset + c);
                    triMasks.Add(mask);
                    editable.Add(meshEditable);
                    if (meshEditable)
                        ++editableTriangles;
                }
            }

            if (included.Count > 0)
                shapes.AddRange(ReadShapes(mdl, included));
        }

        if (editableTriangles == 0)
        {
            DynamicTextureManager.Log.Warning(
                $"No readable meshes use material {materialLabel} — available materials: [{string.Join(", ", models.SelectMany(m => m.Materials).Distinct())}].");
            return null;
        }

        var (parts, partCount) = ComputeParts(positions, indices);
        return new MaterialMesh
        {
            Shapes                 = shapes.ToArray(),
            Positions              = positions.ToArray(),
            Normals                = normals.ToArray(),
            Uvs                    = uvs.ToArray(),
            Indices                = indices.ToArray(),
            TriangleAttributeMasks = triMasks.ToArray(),
            TriangleParts          = parts,
            TriangleEditable       = editable.ToArray(),
            PartCount              = partCount,
            GamePath               = meshGamePath,
        };
    }

    /// <summary>
    /// Shape-key index swaps of the included meshes, in the model's own shape order so the
    /// game's enabled-shape bitmask can be applied by index. Shape values replace an index-
    /// buffer entry (relative to the shape mesh's index block) with a morphed vertex.
    /// </summary>
    private static (string Name, (int IndexPosition, int NewVertex)[] Swaps)[] ReadShapes(MdlFile mdl,
        List<(uint MeshStartIndex, int OurIndexStart, int IndexCount, int VertexOffset, int VertexCount)> included)
    {
        var shapes = new (string, (int, int)[])[mdl.Shapes.Length];
        for (var s = 0; s < mdl.Shapes.Length; ++s)
        {
            var shape = mdl.Shapes[s];
            var swaps = new List<(int, int)>();
            var start = shape.ShapeMeshStartIndex.Length > 0 ? shape.ShapeMeshStartIndex[0] : 0;
            var count = shape.ShapeMeshCount.Length > 0 ? shape.ShapeMeshCount[0] : 0;
            for (var m = start; m < start + count && m < mdl.ShapeMeshes.Length; ++m)
            {
                var shapeMesh = mdl.ShapeMeshes[m];
                foreach (var mesh in included)
                {
                    if (shapeMesh.MeshIndexOffset != mesh.MeshStartIndex)
                        continue;

                    for (var v = shapeMesh.ShapeValueOffset;
                         v < shapeMesh.ShapeValueOffset + shapeMesh.ShapeValueCount && v < mdl.ShapeValues.Length;
                         ++v)
                    {
                        var value = mdl.ShapeValues[v];
                        if (value.BaseIndicesIndex >= mesh.IndexCount || value.ReplacingVertexIndex >= mesh.VertexCount)
                            continue;

                        swaps.Add((mesh.OurIndexStart + value.BaseIndicesIndex, mesh.VertexOffset + value.ReplacingVertexIndex));
                    }
                }
            }

            shapes[s] = (mdl.Shapes[s].ShapeName, swaps.ToArray());
        }

        return shapes;
    }

    /// <summary>
    /// Split the geometry into connected parts: triangles connected through shared vertex
    /// positions (welded at 0.1 mm, so seam-duplicated vertices join) form one part. A decal
    /// limited to its clicked part cannot leak onto overlapping pieces like linings.
    /// </summary>
    private static (int[] Parts, int Count) ComputeParts(List<Vector3> positions, List<int> indices)
    {
        var parent = new int[positions.Count];
        for (var i = 0; i < parent.Length; ++i)
            parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }

        void Union(int a, int b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra != rb)
                parent[ra] = rb;
        }

        // Weld vertices sharing a position, then connect along triangle edges.
        var byPosition = new Dictionary<(long, long, long), int>();
        for (var v = 0; v < positions.Count; ++v)
        {
            var p   = positions[v];
            var key = ((long)Math.Round(p.X * 10000), (long)Math.Round(p.Y * 10000), (long)Math.Round(p.Z * 10000));
            if (byPosition.TryGetValue(key, out var first))
                Union(v, first);
            else
                byPosition[key] = v;
        }

        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            Union(indices[i], indices[i + 1]);
            Union(indices[i], indices[i + 2]);
        }

        var partIds = new Dictionary<int, int>();
        var parts   = new int[indices.Count / 3];
        for (var t = 0; t < parts.Length; ++t)
        {
            var root = Find(indices[t * 3]);
            if (!partIds.TryGetValue(root, out var id))
                partIds[root] = id = partIds.Count;
            parts[t] = id;
        }

        return (parts, partIds.Count);
    }

    private readonly record struct RawVertex(Vector3 Position, Vector3 Normal, Vector2 Uv);

    /// <summary> Decode positions, normals, UV0 and skinning of one mesh from the raw vertex streams. </summary>
    private static bool TryReadMeshVertices(MdlFile mdl, byte[] data, int meshIndex, out RawVertex[] vertices)
    {
        vertices = [];
        if (meshIndex >= mdl.VertexDeclarations.Length)
            return false;

        (byte Stream, byte Offset, MdlFile.VertexType Type)? position = null, normal = null, uv = null;
        foreach (var element in mdl.VertexDeclarations[meshIndex].VertexElements)
        {
            if (element.Stream == 255)
                break;

            var entry = ((byte)element.Stream, element.Offset, (MdlFile.VertexType)element.Type);
            switch ((MdlFile.VertexUsage)element.Usage)
            {
                case MdlFile.VertexUsage.Position:
                    position = entry;
                    break;
                case MdlFile.VertexUsage.Normal:
                    normal = entry;
                    break;
                case MdlFile.VertexUsage.UV when element.UsageIndex == 0:
                    uv = entry;
                    break;
            }
        }

        if (position == null || uv == null)
            return false;

        var mesh    = mdl.Meshes[meshIndex];
        var strides = new[] { mesh.VertexBufferStride1, mesh.VertexBufferStride2, mesh.VertexBufferStride3 };
        var offsets = new[] { mesh.VertexBufferOffset1, mesh.VertexBufferOffset2, mesh.VertexBufferOffset3 };

        long VertexBase((byte Stream, byte Offset, MdlFile.VertexType Type) element, int v)
            => offsets[element.Stream] + (long)v * strides[element.Stream] + element.Offset;

        vertices = new RawVertex[mesh.VertexCount];
        for (var v = 0; v < mesh.VertexCount; ++v)
        {
            var pos = ReadVector3(data, VertexBase(position.Value, v), position.Value.Type);
            var nrm = normal != null ? ReadVector3(data, VertexBase(normal.Value, v), normal.Value.Type) : Vector3.UnitY;
            var tex = ReadVector2(data, VertexBase(uv.Value, v), uv.Value.Type);
            if (pos == null || nrm == null || tex == null)
                return false;

            vertices[v] = new RawVertex(pos.Value, nrm.Value, tex.Value);
        }

        return true;
    }

    private static Vector3? ReadVector3(byte[] data, long p, MdlFile.VertexType type)
    {
        if (p < 0 || p + 16 > data.Length)
            return null;

        return type switch
        {
            MdlFile.VertexType.Single3 or MdlFile.VertexType.Single4
                => new Vector3(BitConverter.ToSingle(data, (int)p), BitConverter.ToSingle(data, (int)p + 4), BitConverter.ToSingle(data, (int)p + 8)),
            MdlFile.VertexType.Half4
                => new Vector3((float)BitConverter.ToHalf(data, (int)p), (float)BitConverter.ToHalf(data, (int)p + 2), (float)BitConverter.ToHalf(data, (int)p + 4)),
            MdlFile.VertexType.NByte4
                => new Vector3(data[p] / 255f * 2f - 1f, data[p + 1] / 255f * 2f - 1f, data[p + 2] / 255f * 2f - 1f),
            _ => Vector3.UnitY,
        };
    }

    private static Vector2? ReadVector2(byte[] data, long p, MdlFile.VertexType type)
    {
        if (p < 0 || p + 8 > data.Length)
            return null;

        return type switch
        {
            MdlFile.VertexType.Single2 or MdlFile.VertexType.Single4
                => new Vector2(BitConverter.ToSingle(data, (int)p), BitConverter.ToSingle(data, (int)p + 4)),
            MdlFile.VertexType.Half2 or MdlFile.VertexType.Half4
                => new Vector2((float)BitConverter.ToHalf(data, (int)p), (float)BitConverter.ToHalf(data, (int)p + 2)),
            _ => Vector2.Zero,
        };
    }

    /// <summary> Island boundaries: UV-quantized edges used by exactly one triangle. Context triangles map into a different texture and are skipped. </summary>
    private static UvLayout BuildLayout(MaterialMesh mesh)
    {
        var edges = new Dictionary<(long, long), (Vector2 A, Vector2 B, int Count)>();
        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            if (!mesh.TriangleEditable[i / 3])
                continue;

            AddEdge(edges, mesh.Uvs[mesh.Indices[i]], mesh.Uvs[mesh.Indices[i + 1]]);
            AddEdge(edges, mesh.Uvs[mesh.Indices[i + 1]], mesh.Uvs[mesh.Indices[i + 2]]);
            AddEdge(edges, mesh.Uvs[mesh.Indices[i + 2]], mesh.Uvs[mesh.Indices[i]]);
        }

        var seams = new List<(Vector2, Vector2)>();
        foreach (var edge in edges.Values)
            if (edge.Count == 1)
                seams.Add((edge.A, edge.B));

        return new UvLayout
        {
            Seams         = seams,
            TriangleCount = mesh.TriangleCount,
        };
    }

    private static void AddEdge(Dictionary<(long, long), (Vector2, Vector2, int)> edges, Vector2 a, Vector2 b)
    {
        var (keyA, keyB) = (Quantize(a), Quantize(b));
        var key = keyA < keyB ? (keyA, keyB) : (keyB, keyA);
        edges[key] = edges.TryGetValue(key, out var existing)
            ? (existing.Item1, existing.Item2, existing.Item3 + 1)
            : (a, b, 1);
    }

    private static long Quantize(Vector2 uv)
        => ((long)Math.Round(uv.X * 8192) << 20) | ((long)Math.Round(uv.Y * 8192) & 0xFFFFF);
}
