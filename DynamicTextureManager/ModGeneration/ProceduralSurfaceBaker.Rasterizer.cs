using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration.Shaders;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

// Stage A of the procedural surface bake: rasterize every accepted triangle into
// per-texel surface fields (position, normal, flow coordinates, chart blends), plus
// the paint-mask and region-weight solves the rasterizer consumes.
public static partial class ProceduralSurfaceBaker
{
    // ------------------------------------------------------------------ stage A: surface fields

    /// <summary> Per-texel surface samples the generators evaluate on. </summary>
    private sealed class SurfaceFields
    {
        public required bool[]    Covered;
        public required Vector3[] Position;
        public required Vector3[] Normal;
        public required float[]   FlowPotential;
        public required float[]   Weight;
        public required float[]   TexelsPerMeter;

        /// <summary>
        /// Flow-aligned flat coordinates in meters (X across the flow, Y along it) from the
        /// texel's three nearest surface charts — geodesic unfoldings computed on the welded
        /// mesh, so they run CONTINUOUSLY across UV seams. Directional patterns evaluate in
        /// all contributing charts and cross-fade, blurring chart boundaries instead of
        /// cutting; three keeps the pick-switch error down where two charts tie.
        /// </summary>
        public required Vector2[] FlowCoordA;

        public required Vector2[] FlowCoordB;

        public required Vector2[] FlowCoordC;

        /// <summary> Normalized shares of charts B and C (A takes the rest). </summary>
        public required float[] BlendB;

        public required float[] BlendC;

        /// <summary> Per-texel pattern offsets of the three charts, decorrelating them. </summary>
        public required float[] OffsetA;

        public required float[] OffsetB;

        public required float[] OffsetC;

        /// <summary>
        /// 1 in the interior, falling to 0 at the mesh's open boundary — directional patterns
        /// fade to a shared world frame there so separate canvases (body and face) meet with
        /// the SAME pattern at their junction ring.
        /// </summary>
        public required float[] SeamBlend;

        /// <summary> Painted markings mask (0 = base color, 1 = highlight), when the style is Painted. </summary>
        public float[]? MarkingPaint;
    }

    /// <summary>
    /// Rasterize every accepted triangle in texture space, interpolating world position and
    /// normal per texel. Directional layers additionally sample the two nearest surface
    /// charts: per triangle the charts are ranked by summed vertex weight (1/d² to the chart
    /// seed), then each texel interpolates both charts' flat coordinates and its cross-fade.
    /// Where UV regions are shared by several triangles the sample with the larger weight
    /// wins, tie-broken by triangle order — deterministic by construction. Single-threaded
    /// on purpose: the overlap resolution depends on visit order.
    /// </summary>
    private static SurfaceFields? RasterizeFields(int width, int height, MaterialMesh mesh, ProceduralSurfaceLayer layer)
    {
        var texels  = width * height;
        var natural = SurfaceFlowField.BodyFlow(mesh);
        var region  = ComputeRegionWeights(mesh, layer);
        // Small companion canvases (the face) skip charts entirely and live in the shared
        // world frame — near the body axis that frame IS a good flow chart, and it makes
        // them match the body at the junction by construction. The seam machinery applies
        // to EVERY kind: the flow potential (stripe bands, tabby markings) must agree
        // where canvases meet. Charts suit only NOISE patterns (fur); cellular scales use
        // one RIGID frame per UV island instead — uniform cells with no interior
        // transitions at all, their only seams the texture's own island borders.
        var worldOnly = MeshExtent(mesh) < 0.35f;
        var charts    = layer.Kind == SurfaceGeneratorKind.Fur && !worldOnly ? SurfaceFlowField.ComputeCharts(mesh) : null;
        // Island frames apply on EVERY canvas (face included): the world-cylinder frame
        // distorts cells on near-horizontal surfaces (collarbone, under the chin), so
        // plates never fall back to it — where two canvases' plate fields meet at the
        // neck, small discrete plates changing lattice reads naturally on its own.
        var islands   = layer.Kind == SurfaceGeneratorKind.Scales ? ComputeIslandFrames(mesh, natural, width, height) : null;
        var boundary  = worldOnly ? null : SurfaceFlowField.BoundaryDistance(mesh);
        var painted   = layer.Markings == FurMarkingStyle.Painted ? ComputePaintMask(mesh, layer.MarkingDabs) : null;
        var fields = new SurfaceFields
        {
            Covered        = new bool[texels],
            Position       = new Vector3[texels],
            Normal         = new Vector3[texels],
            FlowPotential  = new float[texels],
            Weight         = new float[texels],
            TexelsPerMeter = new float[texels],
            FlowCoordA     = new Vector2[texels],
            FlowCoordB     = new Vector2[texels],
            FlowCoordC     = new Vector2[texels],
            BlendB         = new float[texels],
            BlendC         = new float[texels],
            OffsetA        = new float[texels],
            OffsetB        = new float[texels],
            OffsetC        = new float[texels],
            SeamBlend      = new float[texels],
            MarkingPaint   = painted != null ? new float[texels] : null,
        };

        var indices = mesh.Indices;
        var any     = false;

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var triangle = i / 3;
            // Every editable triangle bakes, hidden variants included — full-coverage
            // patterns must exist wherever the surface can appear, and an attribute mask
            // captured on ONE canvas (the body) means something entirely different on a
            // companion canvas (the face) — gating on it once wiped the face bake.
            if (!mesh.TriangleEditable[triangle])
                continue;

            var i0 = indices[i];
            var i1 = indices[i + 1];
            var i2 = indices[i + 2];

            var a = new Vector2(mesh.Uvs[i0].X * width, mesh.Uvs[i0].Y * height);
            var b = new Vector2(mesh.Uvs[i1].X * width, mesh.Uvs[i1].Y * height);
            var c = new Vector2(mesh.Uvs[i2].X * width, mesh.Uvs[i2].Y * height);

            var area = Cross(b - a, c - a);
            if (MathF.Abs(area) < 1e-6f)
                continue;

            // Texel density of this triangle: how many texels one meter of surface covers,
            // from the texel-area / world-area ratio.
            var worldArea = Vector3.Cross(mesh.Positions[i1] - mesh.Positions[i0],
                mesh.Positions[i2] - mesh.Positions[i0]).Length() * 0.5f;
            var texelsPerMeter = worldArea > 1e-12f
                ? MathF.Sqrt(MathF.Abs(area) * 0.5f / worldArea)
                : 0f;

            // The triangle's three dominant charts by summed per-vertex weight — quality
            // (fading smoothly toward each chart's cut locus) over squared seed distance,
            // ties broken by chart index. The weights are SMOOTH per vertex, so a chart
            // hands over gradually wherever it tears or grows distant; any residual
            // pick-switch lands on the third slot where its weight is negligible.
            var chartA = 0;
            var chartB = 0;
            var chartC = 0;
            if (charts != null)
            {
                var bestA = -1f;
                var bestB = -1f;
                var bestC = -1f;
                for (var chart = 0; chart < charts.Count; ++chart)
                {
                    var w = ChartWeight(charts, chart, i0) + ChartWeight(charts, chart, i1) + ChartWeight(charts, chart, i2);
                    if (w > bestA)
                    {
                        (bestC, chartC) = (bestB, chartB);
                        (bestB, chartB) = (bestA, chartA);
                        (bestA, chartA) = (w, chart);
                    }
                    else if (w > bestB)
                    {
                        (bestC, chartC) = (bestB, chartB);
                        (bestB, chartB) = (w, chart);
                    }
                    else if (w > bestC)
                    {
                        (bestC, chartC) = (w, chart);
                    }
                }

                if (bestA <= 0f)
                {
                    // Every chart is torn or unreached here (rare) — take the nearest
                    // reached one rather than sampling garbage from chart 0.
                    var bestD = float.MaxValue;
                    for (var chart = 0; chart < charts.Count; ++chart)
                    {
                        var d0 = charts.Distance[chart][i0];
                        if (d0 < bestD)
                        {
                            bestD  = d0;
                            chartA = chart;
                        }
                    }

                    chartB = chartA;
                    chartC = chartA;
                }
            }

            var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            var maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            var maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            if (minX > maxX || minY > maxY)
                continue;

            var invArea = 1f / area;
            for (var y = minY; y <= maxY; ++y)
            {
                for (var x = minX; x <= maxX; ++x)
                {
                    var p  = new Vector2(x + 0.5f, y + 0.5f);
                    var w0 = Cross(b - p, c - p) * invArea;
                    var w1 = Cross(c - p, a - p) * invArea;
                    var w2 = Cross(a - p, b - p) * invArea;
                    if (w0 < 0f || w1 < 0f || w2 < 0f)
                        continue;

                    var index = y * width + x;

                    // The overlap tie-break compares final weights, so region weights are
                    // part of the weight before the comparison.
                    var weight = 1f;
                    if (region != null)
                        weight *= region[i0] * w0 + region[i1] * w1 + region[i2] * w2;
                    if (fields.Covered[index] && fields.Weight[index] >= weight)
                        continue;

                    fields.Covered[index]  = true;
                    fields.Weight[index]   = weight;
                    fields.Position[index] = mesh.Positions[i0] * w0 + mesh.Positions[i1] * w1 + mesh.Positions[i2] * w2;
                    var normal = mesh.Normals[i0] * w0 + mesh.Normals[i1] * w1 + mesh.Normals[i2] * w2;
                    normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitY;
                    fields.Normal[index]         = normal;
                    fields.TexelsPerMeter[index] = texelsPerMeter;

                    // The band reaches DOWN from the body's top edge far enough to cover
                    // where the face's own rim visibly meets the body (~9 cm of neck) —
                    // both sides are fully in the shared world frame there.
                    var seam = worldOnly
                        ? 0f
                        : boundary != null
                            ? ProceduralFields.Smooth(0.09f, 0.16f,
                                boundary[i0] * w0 + boundary[i1] * w1 + boundary[i2] * w2)
                            : 1f;
                    fields.SeamBlend[index] = seam;

                    if (islands != null)
                    {
                        var frame = islands[triangle];
                        fields.FlowCoordA[index] = frame.MetersPerTexel > 0f
                            ? new Vector2(Vector2.Dot(p, frame.Across), Vector2.Dot(p, frame.Along)) * frame.MetersPerTexel
                            : WorldFrame(fields.Position[index]);
                        fields.OffsetA[index] = frame.MetersPerTexel > 0f ? frame.Offset : 0f;
                    }

                    if (charts != null)
                    {
                        Vector2 InterpV(Vector2[] plane)
                            => plane[i0] * w0 + plane[i1] * w1 + plane[i2] * w2;

                        float WeightAt(int chart)
                            => ChartWeight(charts, chart, i0) * w0 + ChartWeight(charts, chart, i1) * w1
                              + ChartWeight(charts, chart, i2) * w2;

                        fields.FlowCoordA[index] = InterpV(charts.Local[chartA]);
                        fields.FlowCoordB[index] = InterpV(charts.Local[chartB]);
                        fields.FlowCoordC[index] = InterpV(charts.Local[chartC]);
                        fields.OffsetA[index]    = charts.Offset[chartA];
                        fields.OffsetB[index]    = charts.Offset[chartB];
                        fields.OffsetC[index]    = charts.Offset[chartC];

                        var wa = MathF.Max(1e-6f, WeightAt(chartA));
                        var wb = chartB != chartA ? WeightAt(chartB) : 0f;
                        var wc = chartC != chartA && chartC != chartB ? WeightAt(chartC) : 0f;

                        var sum = wa + wb + wc;
                        fields.BlendB[index] = wb / sum;
                        fields.BlendC[index] = wc / sum;
                    }

                    if (painted != null)
                        fields.MarkingPaint![index] = painted[i0] * w0 + painted[i1] * w1 + painted[i2] * w2;

                    // The potential (stripe/tabby banding coordinate) fades to plain world
                    // descent at seams — both canvases band identically where they meet.
                    var meshPotential = natural.Potential[i0] * w0 + natural.Potential[i1] * w1 + natural.Potential[i2] * w2;
                    var descent = -fields.Position[index].Y;
                    fields.FlowPotential[index] = descent + (meshPotential - descent) * seam;

                    any = true;
                }
            }
        }

        return any ? fields : null;
    }

    /// <summary>
    /// Per-vertex mask painted by brush dabs in stroke order — paint dabs take the max
    /// value, restore dabs peel it back — each with a smooth falloff band around its
    /// radius. Null when no dab contributes.
    /// </summary>
    private static float[]? ComputePaintMask(MaterialMesh mesh, List<CoverageDab> dabs)
    {
        if (dabs.Count == 0)
            return null;

        var count = mesh.VertexCount;
        var mask  = new float[count];
        var any   = false;
        foreach (var dab in dabs)
        {
            var center   = new Vector3(dab.X, dab.Y, dab.Z);
            var radius   = MathF.Max(0.005f, dab.Radius);
            var strength = Math.Clamp(dab.Strength, 0f, 1f);
            // Strength is the brush hardness: the full-effect plateau grows with it while
            // the fade band past it shrinks — max is a clean cutoff at the brush edge,
            // low values barely breathe on the pattern.
            var inner  = radius * strength;
            var outer  = MathF.Max(radius * (1.4f - 0.4f * strength), inner + 0.002f);
            var outer2 = outer * outer;

            for (var v = 0; v < count; ++v)
            {
                var d2 = (mesh.Positions[v] - center).LengthSquared();
                if (d2 > outer2)
                    continue;

                var falloff = strength * (1f - ProceduralFields.Smooth(inner, outer, MathF.Sqrt(d2)));
                if (falloff <= 0f)
                    continue;

                mask[v] = dab.Restore ? mask[v] * (1f - falloff) : MathF.Max(mask[v], falloff);
                any     = true;
            }
        }

        return any ? mask : null;
    }

    /// <summary>
    /// Per-vertex coverage weights: 1 minus the painted erase mask — evaluated on EVERY
    /// canvas, so brushing the head erases there too. The face additionally scales by its
    /// coverage slider. Null when nothing reduces coverage.
    /// </summary>
    private static float[]? ComputeRegionWeights(MaterialMesh mesh, ProceduralSurfaceLayer layer)
    {
        var erase = ComputePaintMask(mesh, layer.MaskDabs);
        var faceWeight = mesh.GamePath.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(layer.WeightFace, 0f, 1f)
            : 1f;

        if (erase == null && faceWeight >= 1f)
            return null;

        var result = new float[mesh.VertexCount];
        for (var v = 0; v < result.Length; ++v)
            result[v] = faceWeight * (1f - (erase?[v] ?? 0f));

        return result;
    }

    /// <summary> The shared world frame: a cylinder around the body's vertical axis, identical on every canvas. </summary>
    private static Vector2 WorldFrame(Vector3 pos)
        => new(MathF.Atan2(pos.X, pos.Z) * 0.1f, -pos.Y);

    /// <summary> A chart's smooth per-vertex weight: cut-locus-damped quality over squared seed distance. </summary>
    private static float ChartWeight(SurfaceFlowField.SurfaceCharts charts, int chart, int vertex)
    {
        var quality = charts.Quality[chart][vertex];
        if (quality <= 0f)
            return 0f;

        var distance = charts.Distance[chart][vertex];
        return quality / (distance * distance + 1e-4f);
    }

    private static float MeshExtent(MaterialMesh mesh)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var p in mesh.Positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        var size = max - min;
        return MathF.Max(size.X, MathF.Max(size.Y, size.Z));
    }

    private static float Cross(Vector2 a, Vector2 b)
        => a.X * b.Y - a.Y * b.X;
}
