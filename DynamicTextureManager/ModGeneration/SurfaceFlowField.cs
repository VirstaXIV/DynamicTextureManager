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

    // ------------------------------------------------------------------ natural body flow

    /// <summary> The mesh's natural fur flow, see <see cref="BodyFlow"/>. </summary>
    public sealed class NaturalFlow
    {
        /// <summary> Per raw vertex: unit flow direction in the tangent plane. </summary>
        public required Vector3[] Direction;

        /// <summary> Per raw vertex: geodesic distance from the crown — the along-flow potential. </summary>
        public required float[] Potential;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MaterialMesh, NaturalFlow> BodyFlowCache = new();

    /// <summary>
    /// How fur naturally lies on a body: away from the crown, following the surface — down
    /// the torso AND down each limb, regardless of the bind pose (a plain world-down
    /// direction runs ACROSS the outstretched arms of a T-pose). Computed as the gradient of
    /// the geodesic distance from each connected piece's highest point, on the welded graph —
    /// smooth, seam-free, and deterministic. Anchors override it where they reach.
    /// </summary>
    public static NaturalFlow BodyFlow(MaterialMesh mesh)
    {
        if (BodyFlowCache.TryGetValue(mesh, out var cached))
            return cached;

        var flow = ComputeBodyFlow(mesh);
        BodyFlowCache.AddOrUpdate(mesh, flow);
        return flow;
    }

    private static NaturalFlow ComputeBodyFlow(MaterialMesh mesh)
    {
        var count = mesh.VertexCount;
        var (canonical, neighbors) = mesh.GetOrBuildAdjacency();

        // One crown per connected piece: its highest vertex (ties → lowest index).
        var componentOf = new int[count];
        Array.Fill(componentOf, -1);
        var crowns = new List<int>();
        var stack  = new Stack<int>();
        for (var v = 0; v < count; ++v)
        {
            if (canonical[v] != v || componentOf[v] >= 0)
                continue;

            var component = crowns.Count;
            var crown     = v;
            componentOf[v] = component;
            stack.Push(v);
            while (stack.Count > 0)
            {
                var u = stack.Pop();
                if (mesh.Positions[u].Y > mesh.Positions[crown].Y)
                    crown = u;
                foreach (var n in neighbors[u])
                    if (componentOf[n] < 0)
                    {
                        componentOf[n] = component;
                        stack.Push(n);
                    }
            }

            crowns.Add(crown);
        }

        // Multi-source Dijkstra from every crown, tie-stable like the transport walk.
        var distance = new float[count];
        Array.Fill(distance, float.MaxValue);
        var queue = new PriorityQueue<int, (float Dist, int Vertex)>();
        foreach (var crown in crowns)
        {
            distance[crown] = 0f;
            queue.Enqueue(crown, (0f, crown));
        }

        while (queue.TryDequeue(out var u, out var priority))
        {
            if (priority.Dist > distance[u])
                continue;

            var uPos = mesh.Positions[u];
            foreach (var v in neighbors[u])
            {
                var d = priority.Dist + (mesh.Positions[v] - uPos).Length();
                if (d >= distance[v])
                    continue;

                distance[v] = d;
                queue.Enqueue(v, (d, v));
            }
        }

        // Flow = tangent-plane gradient of the crown distance, estimated from the edges.
        // The potential is normalized per component to "descent below the crown's HEIGHT"
        // (distance minus the crown's Y): near every crown it approximates world descent,
        // so separate canvases (body, face) carry approximately CONTINUOUS potentials at
        // their junction instead of each starting from zero at its own crown.
        var direction = new Vector3[count];
        var potential = new float[count];
        for (var v = 0; v < count; ++v)
        {
            var c = canonical[v];
            potential[v] = distance[c] >= float.MaxValue
                ? -mesh.Positions[v].Y
                : distance[c] - mesh.Positions[crowns[componentOf[c]]].Y;

            var g = Vector3.Zero;
            var cPos = mesh.Positions[c];
            foreach (var u in neighbors[c])
            {
                if (distance[u] >= float.MaxValue || distance[c] >= float.MaxValue)
                    continue;

                var edge = mesh.Positions[u] - cPos;
                var len  = edge.Length();
                if (len < 1e-9f)
                    continue;

                g += edge / len * ((distance[u] - distance[c]) / len);
            }

            var normal = mesh.Normals[v].LengthSquared() > 1e-8f ? Vector3.Normalize(mesh.Normals[v]) : Vector3.UnitY;
            g -= normal * Vector3.Dot(g, normal);
            direction[v] = g.LengthSquared() > 1e-8f ? Vector3.Normalize(g) : FallbackFlow(normal);
        }

        return new NaturalFlow { Direction = direction, Potential = potential };
    }

    /// <summary> Last-resort flow where the gradient degenerates: world-down projected onto the surface. </summary>
    public static Vector3 FallbackFlow(Vector3 normal)
    {
        var down = -Vector3.UnitY - normal * Vector3.Dot(-Vector3.UnitY, normal);
        if (down.LengthSquared() < 1e-4f)
        {
            down = Vector3.UnitZ - normal * Vector3.Dot(Vector3.UnitZ, normal);
            if (down.LengthSquared() < 1e-8f)
                return Vector3.UnitZ;
        }

        return Vector3.Normalize(down);
    }

    // ------------------------------------------------------------------ geodesic path

    /// <summary>
    /// The shortest path along the surface between two picked points, as a polyline of
    /// vertex positions — the paint tools lay dabs along it, so a couple of clicks draw a
    /// stripe that follows the body instead of cutting through space. Empty when the points
    /// don't connect (separate mesh pieces).
    /// </summary>
    public static List<Vector3> GeodesicPath(MaterialMesh mesh, Vector3 from, Vector3 to)
    {
        var (canonical, neighbors) = mesh.GetOrBuildAdjacency();
        var start  = NearestVertex(mesh, from);
        var target = NearestVertex(mesh, to);
        if (start < 0 || target < 0)
            return [];

        start  = canonical[start];
        target = canonical[target];
        if (start == target)
            return [mesh.Positions[start]];

        var count    = mesh.VertexCount;
        var distance = new float[count];
        var parent   = new int[count];
        Array.Fill(distance, float.MaxValue);
        Array.Fill(parent, -1);
        distance[start] = 0f;

        var queue = new PriorityQueue<int, (float Dist, int Vertex)>();
        queue.Enqueue(start, (0f, start));
        while (queue.TryDequeue(out var u, out var priority))
        {
            if (priority.Dist > distance[u])
                continue;

            if (u == target)
                break;

            var uPos = mesh.Positions[u];
            foreach (var v in neighbors[u])
            {
                var d = priority.Dist + (mesh.Positions[v] - uPos).Length();
                if (d >= distance[v])
                    continue;

                distance[v] = d;
                parent[v]   = u;
                queue.Enqueue(v, (d, v));
            }
        }

        if (distance[target] >= float.MaxValue)
            return [];

        var path = new List<Vector3>();
        for (var v = target; v >= 0; v = parent[v])
            path.Add(mesh.Positions[v]);
        path.Reverse();
        return path;
    }

    // ------------------------------------------------------------------ mesh boundary distance

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MaterialMesh, float[]> BoundaryCache = new();

    /// <summary>
    /// Per raw vertex: geodesic distance to the mesh's TOP open boundary — the neck ring,
    /// where another canvas (the face) attaches. float.MaxValue when there is none. Only
    /// boundary edges near the mesh's highest point count: bodies carry many INTERNAL open
    /// edges (unwelded part splits on the legs and elsewhere), and treating those as canvas
    /// junctions once wrapped half the legs in the world-frame blend, smearing the bands.
    /// </summary>
    public static float[] BoundaryDistance(MaterialMesh mesh)
    {
        if (BoundaryCache.TryGetValue(mesh, out var cached))
            return cached;

        var distance = ComputeBoundaryDistance(mesh);
        BoundaryCache.AddOrUpdate(mesh, distance);
        return distance;
    }

    private static float[] ComputeBoundaryDistance(MaterialMesh mesh)
    {
        var count = mesh.VertexCount;
        var (canonical, neighbors) = mesh.GetOrBuildAdjacency();

        var edgeUse = new Dictionary<(int A, int B), int>();
        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var c0 = canonical[mesh.Indices[i]];
            var c1 = canonical[mesh.Indices[i + 1]];
            var c2 = canonical[mesh.Indices[i + 2]];
            void Count(int a, int b)
            {
                if (a == b)
                    return;
                var key = (Math.Min(a, b), Math.Max(a, b));
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
            }

            Count(c0, c1);
            Count(c1, c2);
            Count(c2, c0);
        }

        var maxY = float.MinValue;
        foreach (var p in mesh.Positions)
            maxY = MathF.Max(maxY, p.Y);

        var distance = new float[count];
        Array.Fill(distance, float.MaxValue);
        var queue = new PriorityQueue<int, (float Dist, int Vertex)>();
        var seeds = new SortedSet<int>();
        foreach (var ((a, b), uses) in edgeUse)
        {
            if (uses != 1)
                continue;

            if (mesh.Positions[a].Y < maxY - 0.15f || mesh.Positions[b].Y < maxY - 0.15f)
                continue;

            seeds.Add(a);
            seeds.Add(b);
        }

        foreach (var seed in seeds)
        {
            distance[seed] = 0f;
            queue.Enqueue(seed, (0f, seed));
        }

        while (queue.TryDequeue(out var u, out var priority))
        {
            if (priority.Dist > distance[u])
                continue;

            var uPos = mesh.Positions[u];
            foreach (var v in neighbors[u])
            {
                var d = priority.Dist + (mesh.Positions[v] - uPos).Length();
                if (d >= distance[v])
                    continue;

                distance[v] = d;
                queue.Enqueue(v, (d, v));
            }
        }

        for (var v = 0; v < count; ++v)
            if (canonical[v] != v)
                distance[v] = distance[canonical[v]];

        return distance;
    }

    // ------------------------------------------------------------------ surface charts

    /// <summary>
    /// Flow-aligned flattenings of the whole surface, per raw vertex: each chart unfolds the
    /// mesh around one seed through the transported-frame walk, giving 2D coordinates
    /// (X across the flow, Y along it, meters) that are CONTINUOUS across UV seams — the
    /// walk runs on the position-welded graph. Directional patterns sample the two nearest
    /// charts per texel and cross-fade, so chart boundaries blur instead of cutting.
    /// </summary>
    public sealed class SurfaceCharts
    {
        /// <summary> Per chart, per raw vertex: flow-aligned flat coordinates in meters. </summary>
        public required Vector2[][] Local;

        /// <summary> Per chart, per raw vertex: geodesic distance to the chart seed (MaxValue unreached). </summary>
        public required float[][] Distance;

        /// <summary>
        /// Per chart, per raw vertex: how usable the chart is here, fading SMOOTHLY to 0
        /// toward its cut locus (where the unfolding tears and coordinates jump) — weights
        /// damped by this hand over to a neighboring chart without any hard switch.
        /// </summary>
        public required float[][] Quality;

        /// <summary> Per chart: stable pattern offset decorrelating the charts. </summary>
        public required float[] Offset;

        public int Count
            => Local.Length;
    }

    private const int AutoChartCount = 8;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MaterialMesh, SurfaceCharts> ChartCache = new();

    /// <summary>
    /// Charts auto-seeded via farthest-point sampling, so every part of the mesh — including
    /// disconnected pieces — lies close to some chart. Deterministic: seed selection
    /// tie-breaks on vertex index and the walks are tie-stable. Cached per mesh instance.
    /// </summary>
    public static SurfaceCharts ComputeCharts(MaterialMesh mesh)
    {
        if (ChartCache.TryGetValue(mesh, out var cached))
            return cached;

        var charts = ComputeChartsUncached(mesh);
        ChartCache.AddOrUpdate(mesh, charts);
        return charts;
    }

    private static SurfaceCharts ComputeChartsUncached(MaterialMesh mesh)
    {
        var count = mesh.VertexCount;
        var (canonical, _) = mesh.GetOrBuildAdjacency();

        var locals    = new List<Vector2[]>();
        var distances = new List<float[]>();
        var minDist   = new float[count];
        Array.Fill(minDist, float.MaxValue);

        var natural = BodyFlow(mesh);

        var qualities = new List<float[]>();
        var (_, neighbors) = mesh.GetOrBuildAdjacency();

        void AddChart(int seedVertex, Vector3 dir)
        {
            var normal = mesh.Normals[seedVertex].LengthSquared() > 1e-8f
                ? Vector3.Normalize(mesh.Normals[seedVertex])
                : Vector3.UnitY;
            var planar = dir - normal * Vector3.Dot(dir, normal);
            if (planar.LengthSquared() < 1e-6f)
                planar = Vector3.Cross(normal, Vector3.UnitX).LengthSquared() > 1e-4f
                    ? Vector3.Cross(normal, Vector3.UnitX)
                    : Vector3.Cross(normal, Vector3.UnitY);
            // The walk's LOCAL accumulates (tangent, bitangent) displacement = (X, Y); fur
            // runs +Y along the flow, so the flow direction becomes the bitangent.
            var bitangent = Vector3.Normalize(planar);
            var tangent   = Vector3.Cross(bitangent, normal);

            var walk = TransportWalk(mesh, mesh.Positions[seedVertex], normal, tangent, bitangent, float.MaxValue);

            // Chart quality per canonical vertex: the worst coordinate stretch toward any
            // neighbor. Near the cut locus the unfolding tears (coordinates jump across a
            // short edge) — quality fades smoothly to 0 there instead of switching hard.
            var quality = new float[count];
            for (var v = 0; v < count; ++v)
            {
                if (canonical[v] != v || !walk.Reached[v])
                    continue;

                var stretch = 1f;
                foreach (var u in neighbors[v])
                {
                    if (!walk.Reached[u])
                        continue;

                    var edge = (mesh.Positions[u] - mesh.Positions[v]).Length();
                    if (edge < 1e-6f)
                        continue;

                    stretch = MathF.Max(stretch, (walk.Local[u] - walk.Local[v]).Length() / edge);
                }

                quality[v] = 1f - ProceduralFields.Smooth(2.5f, 5f, stretch);
            }

            var local = new Vector2[count];
            var dist  = new float[count];
            var qual  = new float[count];
            for (var v = 0; v < count; ++v)
            {
                var c = canonical[v];
                local[v] = walk.Local[c];
                dist[v]  = walk.Reached[c] ? walk.Distance[c] : float.MaxValue;
                qual[v]  = quality[c];
                if (dist[v] < minDist[v])
                    minDist[v] = dist[v];
            }

            locals.Add(local);
            distances.Add(dist);
            qualities.Add(qual);
        }

        // Farthest-point top-up: unreached vertices (other mesh pieces) come first, then the
        // vertex farthest along the surface from every existing seed. The very first seed is
        // the highest vertex — the top of the piece.
        while (locals.Count < AutoChartCount)
        {
            var seed = -1;
            if (locals.Count == 0)
            {
                var bestY = float.MinValue;
                for (var v = 0; v < count; ++v)
                    if (canonical[v] == v && mesh.Positions[v].Y > bestY)
                    {
                        bestY = mesh.Positions[v].Y;
                        seed  = v;
                    }
            }
            else
            {
                var best = -1f;
                for (var v = 0; v < count; ++v)
                {
                    if (canonical[v] != v)
                        continue;

                    var d = minDist[v] >= float.MaxValue ? float.PositiveInfinity : minDist[v];
                    if (d > best)
                    {
                        best = d;
                        seed = v;
                        if (float.IsPositiveInfinity(d))
                            break; // lowest-index unreached vertex wins deterministically
                    }
                }

                // Everything already lies within a quarter feature of some seed — done.
                if (seed < 0 || (!float.IsPositiveInfinity(best) && best < 0.05f))
                    break;
            }

            if (seed < 0)
                break;

            AddChart(seed, natural.Direction[seed]);
        }

        var offsets = new float[locals.Count];
        for (var i = 0; i < offsets.Length; ++i)
            offsets[i] = ProceduralFields.Hash01(4177, i, 0, 0) * 173f;

        return new SurfaceCharts
        {
            Local    = locals.ToArray(),
            Distance = distances.ToArray(),
            Quality  = qualities.ToArray(),
            Offset   = offsets,
        };
    }

    private static float Smooth(float a, float b, float t)
        => ProceduralFields.Smooth(a, b, t);
}
