using System;
using System.Collections.Generic;
using System.Numerics;

namespace DynamicTextureManager.ModGeneration;

// Surface charts: flow-aligned flattenings of the whole surface, auto-seeded by
// farthest-point sampling so directional patterns can sample continuous 2D coordinates
// anywhere on the mesh — including across UV seams and disconnected pieces.
public static partial class SurfaceFlowField
{
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
