using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures.Data;
using OtterGui.Services;
using Penumbra.GameData.Files;

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

    public required int PartCount { get; init; }

    /// <summary>
    /// Shape keys in the model's own order (indices must line up with the game's enabled-
    /// shape mask): index-buffer swaps redirecting triangles to morphed alternate vertices.
    /// Applied at pick time so the ray hits the shaped surface the player actually sees.
    /// </summary>
    public required (string Name, (int IndexPosition, int NewVertex)[] Swaps)[] Shapes { get; init; }

    /// <summary> Game path of the model this mesh came from (drives the equipment-slot lookup for attribute masks). </summary>
    public required string GamePath { get; init; }

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
public sealed class ModelUvReader(IDataManager dataManager) : IService
{
    private readonly Dictionary<string, MaterialMesh?> _meshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UvLayout?>     _uvCache   = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> Full bind-pose geometry for a source material, cached; null when the model cannot be read. </summary>
    public MaterialMesh? GetMesh(SourcePath source)
    {
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
                mesh = ReadMesh(new MdlFile(bytes), Path.GetFileName(source.GamePath), source.MdlGamePath);

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

    /// <summary> Extract the LOD-0 geometry of all meshes using the material. </summary>
    private static MaterialMesh? ReadMesh(MdlFile mdl, string materialFileName, string mdlGamePath)
    {
        if (!mdl.Valid || mdl.LodCount == 0)
            return null;

        var data      = mdl.RemainingData;
        var lod       = mdl.Lods[0];
        var positions = new List<Vector3>();
        var normals   = new List<Vector3>();
        var uvs       = new List<Vector2>();
        var indices   = new List<int>();
        var triMasks  = new List<uint>();
        // Included meshes, for mapping shape-value positions into the concatenated arrays.
        var included = new List<(uint MeshStartIndex, int OurIndexStart, int IndexCount, int VertexOffset, int VertexCount)>();

        for (var m = lod.MeshIndex; m < lod.MeshIndex + lod.MeshCount && m < mdl.Meshes.Length; ++m)
        {
            var mesh = mdl.Meshes[m];
            if (mesh.MaterialIndex >= mdl.Materials.Length
             || !mdl.Materials[mesh.MaterialIndex].EndsWith(materialFileName, StringComparison.OrdinalIgnoreCase))
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
                uvs.Add(vertex.Uv);
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
            }
        }

        if (indices.Count == 0)
        {
            DynamicTextureManager.Log.Warning(
                $"Model contains no readable meshes using material {materialFileName} — its materials are [{string.Join(", ", mdl.Materials)}].");
            return null;
        }

        var (parts, partCount) = ComputeParts(positions, indices);
        return new MaterialMesh
        {
            Shapes                 = ReadShapes(mdl, included),
            Positions              = positions.ToArray(),
            Normals                = normals.ToArray(),
            Uvs                    = uvs.ToArray(),
            Indices                = indices.ToArray(),
            TriangleAttributeMasks = triMasks.ToArray(),
            TriangleParts          = parts,
            PartCount              = partCount,
            GamePath               = mdlGamePath,
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

    /// <summary> Island boundaries: UV-quantized edges used by exactly one triangle. </summary>
    private static UvLayout BuildLayout(MaterialMesh mesh)
    {
        var edges = new Dictionary<(long, long), (Vector2 A, Vector2 B, int Count)>();
        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
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
