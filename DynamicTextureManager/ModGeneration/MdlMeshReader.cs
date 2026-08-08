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

/// <summary>
/// Binary .mdl decoding: extracts LOD-0 positions, normals and UV0 from the raw vertex
/// streams, applies shape-key index swaps, wraps UVs into the base tile and splits the
/// geometry into position-welded connected parts. Pure functions of the model bytes —
/// all policy (which materials are editable, race resolution) stays in ModelUvReader.
/// </summary>
internal static class MdlMeshReader
{
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
    internal static MaterialMesh? ReadMeshes(IReadOnlyList<MdlFile> models, Func<string, bool> isEditableMaterial,
        string materialLabel, string meshGamePath, bool includeContext = false, IReadOnlyList<byte>? modelUnits = null)
    {
        var positions = new List<Vector3>();
        var normals   = new List<Vector3>();
        var uvs       = new List<Vector2>();
        var indices   = new List<int>();
        var triMasks  = new List<uint>();
        var editable  = new List<bool>();
        var triUnits  = new List<byte>();
        var shapes    = new List<(string Name, (int IndexPosition, int NewVertex)[] Swaps)>();
        var editableTriangles = 0;

        for (var modelIndex = 0; modelIndex < models.Count; ++modelIndex)
        {
            var mdl  = models[modelIndex];
            var unit = modelUnits != null && modelIndex < modelUnits.Count ? modelUnits[modelIndex] : (byte)0;
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
                    triUnits.Add(unit);
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
            TriangleUnit           = triUnits.ToArray(),
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

    /// <summary> Decode positions, normals and UV0 of one mesh from the raw vertex streams. </summary>
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
        // Bound by what each format actually reads — a fixed 16-byte requirement would
        // spuriously reject a 4-byte element sitting at the very end of a vertex stream.
        var size = type switch
        {
            MdlFile.VertexType.Single3 or MdlFile.VertexType.Single4 => 12,
            MdlFile.VertexType.Half4                                 => 6,
            MdlFile.VertexType.NByte4                                => 4,
            _                                                        => 0,
        };
        if (p < 0 || p + size > data.Length)
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
}
