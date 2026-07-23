using System;
using System.Collections.Generic;
using System.Numerics;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration.Shaders;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Bakes a surface-projected decal into texture space: the decal is projected onto the
/// bind-pose mesh through a curvature-following frame anchored at its placement point (see
/// <see cref="ComputeSurfaceProjection"/>), then every mesh triangle is rasterized in UV space
/// with the projected decal sampled per texel. The result conforms to the geometry, stretches
/// with the actual UV density and continues across UV seams.
/// </summary>
public static class SurfaceDecalBaker
{
    /// <param name="effectSlot">
    /// When set, the bake targets a sibling texture of the same material (normal/mask):
    /// the footprint is identical, but each texel receives the layer's material effect
    /// instead of its colors.
    /// </param>
    public static void Bake(Image<Rgba32> target, Image<Rgba32> decal, MaterialMesh mesh, DecalLayer layer,
        TextureSlot? effectSlot = null)
    {
        var anchor = new Vector3(layer.AnchorX, layer.AnchorY, layer.AnchorZ);
        var normal = new Vector3(layer.NormalX, layer.NormalY, layer.NormalZ);
        if (normal.LengthSquared() < 1e-6f || layer.WorldWidth <= 0f || layer.WorldHeight <= 0f)
            return;

        normal = Vector3.Normalize(normal);
        var (tangent, bitangent) = TangentFrame(normal, layer.RotationDeg);

        // Material effects can cover a larger or smaller area than the decal itself.
        var effectScale = effectSlot != null ? Math.Max(0.01f, layer.EffectScale) : 1f;
        var worldWidth  = layer.WorldWidth * effectScale;
        var worldHeight = layer.WorldHeight * effectScale;

        var threshold = layer.AlphaThresholdByte;
        var opacity   = Math.Clamp(layer.Opacity, 0f, 1f);

        if (effectSlot == null && layer.IdRemap && (layer.PaletteRows.Count == 0 || layer.PaletteRows.Count != layer.PaletteColors.Count))
        {
            DynamicTextureManager.Log.Warning("Surface colorset decal has no allocated rows, layer skipped.");
            return;
        }

        var decalPixels = new Rgba32[decal.Width * decal.Height];
        decal.CopyPixelDataTo(decalPixels);

        var projection = ComputeSurfaceProjection(mesh, anchor, normal, tangent, bitangent, WalkRadius(worldWidth, worldHeight));

        // Rasterize the same morphed surface that was clicked when placing.
        var indices = mesh.IndicesWithShapes(layer.SurfaceShapes);

        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            // Stay on the stamped mesh part and off geometry that was hidden at stamp time —
            // overlapping pieces (linings, variant panels) must not catch the projection.
            // Context geometry (other materials of the model set) has UVs in a different
            // texture and must never be rasterized into this one.
            var triangle = i / 3;
            if (!mesh.TriangleEditable[triangle])
                continue;
            if (layer.SurfaceLimitToPart && layer.SurfacePart >= 0 && mesh.TriangleParts[triangle] != layer.SurfacePart)
                continue;
            if ((mesh.TriangleAttributeMasks[triangle] & ~layer.SurfaceAttributes) != 0)
                continue;

            var i0 = indices[i];
            var i1 = indices[i + 1];
            var i2 = indices[i + 2];

            // Vertices outside the walk radius (too far from the anchor along the surface)
            // never receive the decal — this replaces both the old flat depth clamp and the
            // projector-facing cull, and correctly follows curvature instead of assuming a
            // single flat plane.
            if (!projection.Reached[i0] || !projection.Reached[i1] || !projection.Reached[i2])
                continue;

            // Decal-plane coordinates (0..1 across the decal) per vertex, in world units
            // converted to decal space.
            var d0 = new Vector2(projection.Local[i0].X / worldWidth + 0.5f, projection.Local[i0].Y / worldHeight + 0.5f);
            var d1 = new Vector2(projection.Local[i1].X / worldWidth + 0.5f, projection.Local[i1].Y / worldHeight + 0.5f);
            var d2 = new Vector2(projection.Local[i2].X / worldWidth + 0.5f, projection.Local[i2].Y / worldHeight + 0.5f);

            if ((d0.X < 0f && d1.X < 0f && d2.X < 0f) || (d0.X > 1f && d1.X > 1f && d2.X > 1f)
             || (d0.Y < 0f && d1.Y < 0f && d2.Y < 0f) || (d0.Y > 1f && d1.Y > 1f && d2.Y > 1f))
                continue;

            RasterizeTriangle(target, decalPixels, decal.Width, decal.Height,
                mesh.Uvs[i0], mesh.Uvs[i1], mesh.Uvs[i2], d0, d1, d2, threshold, opacity, layer, effectSlot);
        }
    }

    /// <summary>
    /// Whether a decal's footprint touches ANY editable triangle of a (possibly different)
    /// mesh — used to decide whether an overlay-part texture (nails, accents) needs a companion
    /// bake job synthesized for a body-skin decal at all. Mirrors <see cref="Bake"/>'s own
    /// triangle-acceptance test (reached by the walk, then inside the [0,1]x[0,1] decal square)
    /// without actually rasterizing anything.
    /// </summary>
    public static bool FootprintTouches(MaterialMesh mesh, DecalLayer layer)
    {
        var anchor = new Vector3(layer.AnchorX, layer.AnchorY, layer.AnchorZ);
        var normal = new Vector3(layer.NormalX, layer.NormalY, layer.NormalZ);
        if (normal.LengthSquared() < 1e-6f || layer.WorldWidth <= 0f || layer.WorldHeight <= 0f)
            return false;

        normal = Vector3.Normalize(normal);
        var (tangent, bitangent) = TangentFrame(normal, layer.RotationDeg);
        var projection = ComputeSurfaceProjection(mesh, anchor, normal, tangent, bitangent, WalkRadius(layer.WorldWidth, layer.WorldHeight));

        var indices = mesh.IndicesWithShapes(layer.SurfaceShapes);
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var triangle = i / 3;
            if (!mesh.TriangleEditable[triangle])
                continue;

            var i0 = indices[i];
            var i1 = indices[i + 1];
            var i2 = indices[i + 2];
            if (!projection.Reached[i0] || !projection.Reached[i1] || !projection.Reached[i2])
                continue;

            var d0 = new Vector2(projection.Local[i0].X / layer.WorldWidth + 0.5f, projection.Local[i0].Y / layer.WorldHeight + 0.5f);
            var d1 = new Vector2(projection.Local[i1].X / layer.WorldWidth + 0.5f, projection.Local[i1].Y / layer.WorldHeight + 0.5f);
            var d2 = new Vector2(projection.Local[i2].X / layer.WorldWidth + 0.5f, projection.Local[i2].Y / layer.WorldHeight + 0.5f);
            if ((d0.X < 0f && d1.X < 0f && d2.X < 0f) || (d0.X > 1f && d1.X > 1f && d2.X > 1f)
             || (d0.Y < 0f && d1.Y < 0f && d2.Y < 0f) || (d0.Y > 1f && d1.Y > 1f && d2.Y > 1f))
                continue;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Per-vertex decal-space coordinates that follow the mesh surface instead of a single
    /// flat plane: a radius-bounded Dijkstra walk from the anchor over the position-welded
    /// vertex graph (<see cref="MaterialMesh.GetOrBuildAdjacency"/>), accumulating (tangent,
    /// bitangent) displacement in world units with the local frame parallel-transported (the
    /// minimal rotation between consecutive vertex normals) at each step.
    /// </summary>
    /// <remarks>
    /// A flat plane is only exact infinitesimally close to the anchor — on curved body parts
    /// (limbs, torso) it increasingly stretches the decal the further a point lies from the
    /// anchor, which is what made tattoos look warped/broken on flat-looking-but-curved
    /// surfaces (2026-07 investigation). This same computation feeds both <see cref="Bake"/>
    /// and the live viewport preview so they always agree. Vertices outside
    /// <paramref name="maxWalkDistance"/> — measured along the surface, not as the crow flies —
    /// are left unreached; callers treat them as outside the decal's footprint.
    /// </remarks>
    public static SurfaceProjection ComputeSurfaceProjection(MaterialMesh mesh, Vector3 anchor, Vector3 normal,
        Vector3 tangent, Vector3 bitangent, float maxWalkDistance)
    {
        var local   = new Vector2[mesh.VertexCount];
        var reached = new bool[mesh.VertexCount];
        var (canonical, neighbors) = mesh.GetOrBuildAdjacency();

        var seed = NearestVertex(mesh, anchor);
        if (seed < 0)
            return new SurfaceProjection { Local = local, Reached = reached };

        var seedCanonical = canonical[seed];
        var toSeed        = mesh.Positions[seedCanonical] - anchor;
        var seedDist       = toSeed.Length();
        if (seedDist > maxWalkDistance)
            return new SurfaceProjection { Local = local, Reached = reached };

        var bestDistance = new float[mesh.VertexCount];
        Array.Fill(bestDistance, float.MaxValue);
        var canonLocal    = new Vector2[mesh.VertexCount];
        var canonTangent  = new Vector3[mesh.VertexCount];
        var canonBitangent = new Vector3[mesh.VertexCount];
        var canonReached  = new bool[mesh.VertexCount];

        var planarSeed = toSeed - normal * Vector3.Dot(toSeed, normal);
        canonLocal[seedCanonical]     = new Vector2(Vector3.Dot(planarSeed, tangent), Vector3.Dot(planarSeed, bitangent));
        canonTangent[seedCanonical]   = tangent;
        canonBitangent[seedCanonical] = bitangent;
        canonReached[seedCanonical]   = true;
        bestDistance[seedCanonical]   = seedDist;

        var queue = new PriorityQueue<int, float>();
        queue.Enqueue(seedCanonical, seedDist);

        while (queue.TryDequeue(out var u, out var du))
        {
            if (du > bestDistance[u])
                continue; // stale entry superseded by a shorter path already processed

            var uTangent   = canonTangent[u];
            var uBitangent = canonBitangent[u];
            var uNormal    = mesh.Normals[u].LengthSquared() > 1e-8f ? Vector3.Normalize(mesh.Normals[u]) : normal;
            var uLocal     = canonLocal[u];
            var uPos       = mesh.Positions[u];

            foreach (var v in neighbors[u])
            {
                var edge    = mesh.Positions[v] - uPos;
                var edgeLen = edge.Length();
                if (edgeLen < 1e-9f)
                    continue;

                var newDist = du + edgeLen;
                if (newDist > maxWalkDistance || newDist >= bestDistance[v])
                    continue;

                var planar = edge - uNormal * Vector3.Dot(edge, uNormal);
                canonLocal[v]     = uLocal + new Vector2(Vector3.Dot(planar, uTangent), Vector3.Dot(planar, uBitangent));
                var vNormal       = mesh.Normals[v].LengthSquared() > 1e-8f ? Vector3.Normalize(mesh.Normals[v]) : uNormal;
                var rotation      = MinimalRotation(uNormal, vNormal);
                canonTangent[v]   = Vector3.Normalize(Vector3.Transform(uTangent, rotation));
                canonBitangent[v] = Vector3.Normalize(Vector3.Transform(uBitangent, rotation));
                canonReached[v]   = true;
                bestDistance[v]   = newDist;
                queue.Enqueue(v, newDist);
            }
        }

        for (var i = 0; i < mesh.VertexCount; ++i)
        {
            var c = canonical[i];
            if (!canonReached[c])
                continue;

            local[i]   = canonLocal[c];
            reached[i] = true;
        }

        return new SurfaceProjection { Local = local, Reached = reached };
    }

    /// <summary>
    /// How far <see cref="ComputeSurfaceProjection"/> should walk from the anchor to safely
    /// cover a decal of this size. Must exceed the decal's half-diagonal (the farthest a
    /// corner can be from the anchor at any rotation) by a wide margin: the walk measures
    /// distance along mesh EDGES, which on an irregular or elongated local triangulation can
    /// noticeably exceed the true straight-line/geodesic distance — and does so unevenly by
    /// direction, so an insufficient margin clips one side of the decal before the other
    /// (observed 2026-07: a heart's right lobe clipped while the left stayed intact at a 1.8x
    /// margin). The flat pad on top covers small decals on coarse/sparse mesh regions where
    /// even a generous multiplier isn't enough absolute slack.
    /// </summary>
    public static float WalkRadius(float worldWidth, float worldHeight)
        => 0.5f * MathF.Sqrt(worldWidth * worldWidth + worldHeight * worldHeight) * 2.5f + 0.03f;

    /// <summary> The nearest raw vertex index to a point, by straight-line distance — the walk's seed. </summary>
    private static int NearestVertex(MaterialMesh mesh, Vector3 point)
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
    private static Quaternion MinimalRotation(Vector3 from, Vector3 to)
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

    /// <summary> Per-vertex decal-space projection, see <see cref="ComputeSurfaceProjection"/>. </summary>
    public sealed class SurfaceProjection
    {
        public required Vector2[] Local { get; init; }
        public required bool[]    Reached { get; init; }
    }

    /// <summary> Rasterize one triangle in texture space, sampling the decal through the interpolated projection coordinates. </summary>
    private static void RasterizeTriangle(Image<Rgba32> target, Rgba32[] decal, int decalWidth, int decalHeight,
        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 d0, Vector2 d1, Vector2 d2,
        byte threshold, float opacity, DecalLayer layer, TextureSlot? effectSlot)
    {
        var a = new Vector2(uv0.X * target.Width, uv0.Y * target.Height);
        var b = new Vector2(uv1.X * target.Width, uv1.Y * target.Height);
        var c = new Vector2(uv2.X * target.Width, uv2.Y * target.Height);

        var area = Cross(b - a, c - a);
        if (MathF.Abs(area) < 1e-6f)
            return;

        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var maxX = Math.Min(target.Width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var maxY = Math.Min(target.Height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (minX > maxX || minY > maxY)
            return;

        for (var y = minY; y <= maxY; ++y)
        {
            for (var x = minX; x <= maxX; ++x)
            {
                var p  = new Vector2(x + 0.5f, y + 0.5f);
                var w0 = Cross(b - p, c - p) / area;
                var w1 = Cross(c - p, a - p) / area;
                var w2 = 1f - w0 - w1;
                if (w0 < 0f || w1 < 0f || w2 < 0f)
                    continue;

                var local = d0 * w0 + d1 * w1 + d2 * w2;
                if (local.X is < 0f or > 1f || local.Y is < 0f or > 1f)
                    continue;

                var sample = SampleBilinear(decal, decalWidth, decalHeight, local.X, local.Y);
                if (effectSlot is { } slot)
                {
                    var pixel = target[x, y];
                    if (TextureCompositor.ApplyEffectPixel(ref pixel, sample, threshold, layer, slot))
                        target[x, y] = pixel;
                }
                else if (layer.IdRemap)
                {
                    if (sample.A < threshold)
                        continue;

                    var row   = layer.PaletteRows[DecalQuantizer.NearestIndex(sample, layer.PaletteColors)];
                    var pixel = target[x, y];
                    IdMapTexel.StampRow(ref pixel, row, sample.A, layer.WriteBlendFromAlpha);
                    target[x, y] = pixel;
                }
                else
                {
                    sample = DecalQuantizer.ApplyTint(sample, layer);
                    var alpha = sample.A / 255f * opacity;
                    if (alpha <= 0f)
                        continue;

                    var pixel = target[x, y];
                    pixel.R      = (byte)Math.Clamp((int)Math.Round(sample.R * alpha + pixel.R * (1f - alpha)), 0, 255);
                    pixel.G      = (byte)Math.Clamp((int)Math.Round(sample.G * alpha + pixel.G * (1f - alpha)), 0, 255);
                    pixel.B      = (byte)Math.Clamp((int)Math.Round(sample.B * alpha + pixel.B * (1f - alpha)), 0, 255);
                    target[x, y] = pixel;
                }
            }
        }
    }

    /// <summary>
    /// The decal's axes on the surface: at rotation 0 the decal is upright on the gear.
    /// The axis directions are calibrated empirically against in-game results (2026-07-21:
    /// the analytic "viewer frame" rendered upside down) — change only with a visual check.
    /// </summary>
    public static (Vector3 Tangent, Vector3 Bitangent) TangentFrame(Vector3 normal, float rotationDeg)
    {
        var reference = Math.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
        var tangent   = -Vector3.Normalize(Vector3.Cross(normal, reference));
        var bitangent = Vector3.Cross(normal, tangent);
        if (Math.Abs(rotationDeg) > 0.001f)
        {
            var rotation = Quaternion.CreateFromAxisAngle(normal, rotationDeg * MathF.PI / 180f);
            tangent   = Vector3.Transform(tangent, rotation);
            bitangent = Vector3.Transform(bitangent, rotation);
        }

        return (tangent, bitangent);
    }

    private static float Cross(Vector2 a, Vector2 b)
        => a.X * b.Y - a.Y * b.X;

    /// <summary> Bilinear RGBA sample of a pixel buffer at normalized coordinates, shared with the 3D viewport. </summary>
    public static Rgba32 SampleBilinear(Rgba32[] pixels, int width, int height, float u, float v)
    {
        var fx = u * (width - 1);
        var fy = v * (height - 1);
        var x0 = Math.Clamp((int)fx, 0, width - 1);
        var y0 = Math.Clamp((int)fy, 0, height - 1);
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var tx = fx - x0;
        var ty = fy - y0;

        var c00 = pixels[y0 * width + x0];
        var c10 = pixels[y0 * width + x1];
        var c01 = pixels[y1 * width + x0];
        var c11 = pixels[y1 * width + x1];

        float Lerp2(float a, float b, float t)
            => a + (b - a) * t;

        byte Channel(Func<Rgba32, byte> select)
            => (byte)Math.Clamp((int)Math.Round(
                Lerp2(Lerp2(select(c00), select(c10), tx), Lerp2(select(c01), select(c11), tx), ty)), 0, 255);

        return new Rgba32(Channel(p => p.R), Channel(p => p.G), Channel(p => p.B), Channel(p => p.A));
    }
}
