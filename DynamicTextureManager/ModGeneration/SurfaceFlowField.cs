using System;
using System.Collections.Generic;
using System.Numerics;
using DynamicTextureManager.DTextures.Data;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// The geodesic flow field procedural surface layers orient by: from every guide anchor a
/// parallel-transported tangent frame is walked over the position-welded vertex graph (the
/// same walk surface decals project through, shared via <see cref="TransportWalk"/>), giving
/// each vertex that anchor's flow direction and geodesic distance. Multiple anchors blend by
/// inverse squared distance; exclusion anchors instead fade the pattern out around themselves.
/// The walk is deterministic: adjacency neighbor lists are sorted and the Dijkstra priority
/// carries the vertex index as a tie-break, so equal-distance pops always order the same way.
/// </summary>
public static class SurfaceFlowField
{
    /// <summary> Per-canonical-vertex result of one transported walk from a seed frame. </summary>
    public sealed class TransportField
    {
        /// <summary> Geodesic distance along mesh edges; <see cref="float.MaxValue"/> where unreached. </summary>
        public required float[] Distance;

        /// <summary> The transported tangent — an anchor's flow direction carried across the surface. </summary>
        public required Vector3[] Tangent;

        public required Vector3[] Bitangent;

        /// <summary> Accumulated (tangent, bitangent) displacement in world units — the decal-plane coordinates. </summary>
        public required Vector2[] Local;

        public required bool[] Reached;
    }

    /// <summary>
    /// Radius-bounded Dijkstra from the vertex nearest the anchor, accumulating tangent-plane
    /// displacement and parallel-transporting the frame by the minimal rotation between
    /// consecutive vertex normals. Results are per CANONICAL vertex (see
    /// <see cref="MaterialMesh.GetOrBuildAdjacency"/>); map through Canonical for raw indices.
    /// </summary>
    public static TransportField TransportWalk(MaterialMesh mesh, Vector3 anchor, Vector3 normal,
        Vector3 tangent, Vector3 bitangent, float maxWalkDistance)
    {
        var count = mesh.VertexCount;
        var field = new TransportField
        {
            Distance  = new float[count],
            Tangent   = new Vector3[count],
            Bitangent = new Vector3[count],
            Local     = new Vector2[count],
            Reached   = new bool[count],
        };
        Array.Fill(field.Distance, float.MaxValue);

        var (canonical, neighbors) = mesh.GetOrBuildAdjacency();

        var seed = NearestVertex(mesh, anchor);
        if (seed < 0)
            return field;

        var seedCanonical = canonical[seed];
        var toSeed        = mesh.Positions[seedCanonical] - anchor;
        var seedDist      = toSeed.Length();
        if (seedDist > maxWalkDistance)
            return field;

        var planarSeed = toSeed - normal * Vector3.Dot(toSeed, normal);
        field.Local[seedCanonical]     = new Vector2(Vector3.Dot(planarSeed, tangent), Vector3.Dot(planarSeed, bitangent));
        field.Tangent[seedCanonical]   = tangent;
        field.Bitangent[seedCanonical] = bitangent;
        field.Reached[seedCanonical]   = true;
        field.Distance[seedCanonical]  = seedDist;

        // The priority carries the vertex index so equal-distance pops are ordered — with the
        // sorted adjacency this makes the whole walk independent of hash/iteration order.
        var queue = new PriorityQueue<int, (float Dist, int Vertex)>();
        queue.Enqueue(seedCanonical, (seedDist, seedCanonical));

        while (queue.TryDequeue(out var u, out var priority))
        {
            if (priority.Dist > field.Distance[u])
                continue; // stale entry superseded by a shorter path already processed

            var uTangent   = field.Tangent[u];
            var uBitangent = field.Bitangent[u];
            var uNormal    = mesh.Normals[u].LengthSquared() > 1e-8f ? Vector3.Normalize(mesh.Normals[u]) : normal;
            var uLocal     = field.Local[u];
            var uPos       = mesh.Positions[u];

            foreach (var v in neighbors[u])
            {
                var edge    = mesh.Positions[v] - uPos;
                var edgeLen = edge.Length();
                if (edgeLen < 1e-9f)
                    continue;

                var newDist = priority.Dist + edgeLen;
                if (newDist > maxWalkDistance || newDist >= field.Distance[v])
                    continue;

                var planar = edge - uNormal * Vector3.Dot(edge, uNormal);
                field.Local[v]     = uLocal + new Vector2(Vector3.Dot(planar, uTangent), Vector3.Dot(planar, uBitangent));
                var vNormal        = mesh.Normals[v].LengthSquared() > 1e-8f ? Vector3.Normalize(mesh.Normals[v]) : uNormal;
                var rotation       = MinimalRotation(uNormal, vNormal);
                field.Tangent[v]   = Vector3.Normalize(Vector3.Transform(uTangent, rotation));
                field.Bitangent[v] = Vector3.Normalize(Vector3.Transform(uBitangent, rotation));
                field.Reached[v]   = true;
                field.Distance[v]  = newDist;
                queue.Enqueue(v, (newDist, v));
            }
        }

        return field;
    }

    /// <summary> The nearest raw vertex index to a point, by straight-line distance — the walk's seed. </summary>
    public static int NearestVertex(MaterialMesh mesh, Vector3 point)
    {
        var best     = -1;
        var bestDist = float.MaxValue;
        for (var i = 0; i < mesh.Positions.Length; ++i)
        {
            var dist = (mesh.Positions[i] - point).LengthSquared();
            if (dist < bestDist)
            {
                bestDist = dist;
                best     = i;
            }
        }

        return best;
    }

    /// <summary> The shortest-arc rotation that maps one direction onto another. </summary>
    public static Quaternion MinimalRotation(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot > 0.9999f)
            return Quaternion.Identity;

        if (dot < -0.9999f)
        {
            var axis = Vector3.Cross(from, MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
            axis = Vector3.Normalize(axis);
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }

        var cross = Vector3.Cross(from, to);
        var q     = new Quaternion(cross.X, cross.Y, cross.Z, 1f + dot);
        return Quaternion.Normalize(q);
    }

    // ------------------------------------------------------------------ blended vertex flow

    /// <summary> Per RAW vertex blended flow, ready for triangle interpolation in the baker. </summary>
    public sealed class VertexFlow
    {
        /// <summary> Blended flow direction in the vertex tangent plane; zero where no anchor reaches. </summary>
        public required Vector3[] Direction;

        /// <summary> Blended geodesic distance from the steering anchors — the stripe-banding coordinate. </summary>
        public required float[] Potential;

        /// <summary> Product of all exclusion-anchor fades, 1 = fully visible. </summary>
        public required float[] Exclusion;

        public required bool[] HasFlow;
    }

    // Steering walks cover the whole mesh and cost O(V log V) each — remember them per anchor
    // so dragging one anchor only recomputes that anchor. Entries die with the mesh.
    private const int CachePerMesh = 8;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MaterialMesh,
        Dictionary<string, (TransportField Field, long Seq)>> WalkCache = new();

    private static long _walkSeq;

    /// <summary> Blend all anchors into per-raw-vertex flow. Null when there are no anchors at all. </summary>
    public static VertexFlow? ComputeVertexFlow(MaterialMesh mesh, IReadOnlyList<FlowAnchor> anchors)
    {
        if (anchors.Count == 0)
            return null;

        var count = mesh.VertexCount;
        var (canonical, _) = mesh.GetOrBuildAdjacency();

        var direction = new Vector3[count];
        var potential = new float[count];
        var exclusion = new float[count];
        var hasFlow   = new bool[count];
        Array.Fill(exclusion, 1f);

        var weightSum = new float[count];
        var dirSum    = new Vector3[count];
        var potSum    = new float[count];

        foreach (var anchor in anchors)
        {
            var normal = new Vector3(anchor.NormalX, anchor.NormalY, anchor.NormalZ);
            var dir    = new Vector3(anchor.DirX, anchor.DirY, anchor.DirZ);
            if (normal.LengthSquared() < 1e-6f)
                continue;

            normal = Vector3.Normalize(normal);
            var planar = dir - normal * Vector3.Dot(dir, normal);
            if (planar.LengthSquared() < 1e-6f)
                planar = Vector3.Cross(normal, Vector3.UnitX).LengthSquared() > 1e-4f
                    ? Vector3.Cross(normal, Vector3.UnitX)
                    : Vector3.Cross(normal, Vector3.UnitY);
            var tangent   = Vector3.Normalize(planar);
            var bitangent = Vector3.Cross(normal, tangent);

            var maxDist = anchor.Exclude ? anchor.Radius + anchor.Feather + 0.01f : float.MaxValue;
            var field   = GetOrWalk(mesh, anchor, normal, tangent, bitangent, maxDist);

            if (anchor.Exclude)
            {
                for (var v = 0; v < count; ++v)
                {
                    var c = canonical[v];
                    if (!field.Reached[c])
                        continue;

                    exclusion[v] *= Smooth(anchor.Radius, anchor.Radius + Math.Max(1e-4f, anchor.Feather), field.Distance[c]);
                }

                continue;
            }

            var strength = Math.Max(0f, anchor.Strength);
            if (strength <= 0f)
                continue;

            for (var v = 0; v < count; ++v)
            {
                var c = canonical[v];
                if (!field.Reached[c])
                    continue;

                var w = strength / (field.Distance[c] * field.Distance[c] + 1e-4f);
                weightSum[v] += w;
                dirSum[v]    += field.Tangent[c] * w;
                potSum[v]    += field.Distance[c] * w;
            }
        }

        for (var v = 0; v < count; ++v)
        {
            if (weightSum[v] <= 0f)
                continue;

            var normal = mesh.Normals[v].LengthSquared() > 1e-8f ? Vector3.Normalize(mesh.Normals[v]) : Vector3.UnitY;
            var d      = dirSum[v] / weightSum[v];
            d -= normal * Vector3.Dot(d, normal);
            if (d.LengthSquared() < 1e-8f)
                continue; // opposing anchors cancel here — the baker's default flow takes over

            direction[v] = Vector3.Normalize(d);
            potential[v] = potSum[v] / weightSum[v];
            hasFlow[v]   = true;
        }

        return new VertexFlow
        {
            Direction = direction,
            Potential = potential,
            Exclusion = exclusion,
            HasFlow   = hasFlow,
        };
    }

    private static TransportField GetOrWalk(MaterialMesh mesh, FlowAnchor anchor, Vector3 normal,
        Vector3 tangent, Vector3 bitangent, float maxDist)
    {
        var key = FormattableString.Invariant(
            $"{anchor.PosX:F5},{anchor.PosY:F5},{anchor.PosZ:F5}|{normal.X:F4},{normal.Y:F4},{normal.Z:F4}|{tangent.X:F4},{tangent.Y:F4},{tangent.Z:F4}|{maxDist:F4}");
        var table = WalkCache.GetOrCreateValue(mesh);

        lock (table)
        {
            if (table.TryGetValue(key, out var hit))
            {
                table[key] = (hit.Field, ++_walkSeq);
                return hit.Field;
            }
        }

        var field = TransportWalk(mesh, new Vector3(anchor.PosX, anchor.PosY, anchor.PosZ), normal, tangent, bitangent, maxDist);

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

            table[key] = (field, ++_walkSeq);
        }

        return field;
    }

    private static float Smooth(float a, float b, float t)
        => ProceduralFields.Smooth(a, b, t);
}
