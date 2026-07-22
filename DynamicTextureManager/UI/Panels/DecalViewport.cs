using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration;
using OtterGui.Text;
using SixLabors.ImageSharp.PixelFormats;

namespace DynamicTextureManager.UI.Panels;

/// <summary>
/// Everything the viewport samples to shade the mesh with the material's real look: the
/// composited diffuse, the composited id map and the 32 resolved colorset row diffuse
/// colors (the dTexture's edits applied). Any part may be null and falls back to gray.
/// </summary>
public sealed record ViewportShading(DecodedTexture? Diffuse, DecodedTexture? IdMap, Vector3[]? RowDiffuse);

/// <summary>
/// The 3D preview of the selected material: the gear mesh software-rendered in its bind
/// pose — the exact space the bake works in — shaded with the composited textures and the
/// live colorset colors, so it doubles as the main preview of the decals in their set
/// colors. Binding a decal layer turns it into placement mode: left-drag stamps and moves
/// the decal, Ctrl+wheel resizes it and Shift+wheel rotates it. Right-drag orbits,
/// middle-drag pans, wheel zooms. Renders embedded in the Decals tab or popped out.
/// </summary>
public sealed class DecalViewport(ITextureProvider textureProvider) : IDisposable
{
    private const int RenderSize = 768;

    private bool          _open;
    private bool          _poppedOut;
    private DTexture?     _dTexture;
    private DecalLayer?   _layer;
    private MaterialMesh? _mesh;
    private uint          _visibleAttributes = uint.MaxValue;
    private Action?       _onChanged;

    private ViewportShading? _shading;
    private bool             _highlightDecal;

    private Rgba32[]? _decalPixels;
    private int       _decalWidth;
    private int       _decalHeight;

    private float   _yaw      = 0.3f;
    private float   _pitch    = 0.1f;
    private float   _distance = 1f;
    private Vector3 _target;

    private IDalamudTextureWrap? _wrap;
    private bool                 _renderDirty = true;
    private bool                 _editDirty;
    private Matrix4x4            _lastViewProjection = Matrix4x4.Identity;

    public void Dispose()
        => _wrap?.Dispose();

    public bool IsOpenFor(DecalLayer layer)
        => _open && ReferenceEquals(_layer, layer);

    /// <summary> The layer currently bound for placement, null in view mode. </summary>
    public DecalLayer? PlacementLayer
        => _open ? _layer : null;

    /// <summary>
    /// Show the viewport for a material's mesh in view mode. Idempotent per frame: the
    /// camera and any bound placement layer survive as long as the mesh stays the same.
    /// </summary>
    public void Open(DTexture dTexture, MaterialMesh mesh, uint visibleAttributes)
    {
        var changed = !ReferenceEquals(_mesh, mesh) || !ReferenceEquals(_dTexture, dTexture);
        _dTexture = dTexture;
        if (_visibleAttributes != visibleAttributes)
            _renderDirty = true;
        _visibleAttributes = visibleAttributes;
        if (changed)
        {
            _mesh        = mesh;
            _layer       = null;
            _onChanged   = null;
            _renderDirty = true;
            FrameCamera();
        }

        _open = true;
    }

    /// <summary> Bind a decal layer for interactive placement on the currently shown mesh. </summary>
    public void BeginPlacement(DecalLayer layer, string decalPath, Action onChanged)
    {
        _layer       = layer;
        _onChanged   = onChanged;
        _renderDirty = true;
        LoadDecal(decalPath);
    }

    /// <summary> Return to view mode, committing any pending placement edit. </summary>
    public void EndPlacement()
    {
        if (_editDirty)
        {
            _editDirty = false;
            _onChanged?.Invoke();
        }

        _layer       = null;
        _onChanged   = null;
        _renderDirty = true;
    }

    /// <summary> Swap in new shading buffers; re-renders only when something actually changed. </summary>
    public void UpdateShading(ViewportShading? shading)
    {
        if (ReferenceEquals(_shading?.Diffuse, shading?.Diffuse)
         && ReferenceEquals(_shading?.IdMap, shading?.IdMap)
         && ReferenceEquals(_shading?.RowDiffuse, shading?.RowDiffuse))
            return;

        _shading     = shading;
        _renderDirty = true;
    }

    public void Close()
    {
        EndPlacement();
        _open = false;
    }

    private void LoadDecal(string path)
    {
        _decalPixels = null;
        try
        {
            if (!File.Exists(path))
                return;

            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
            _decalWidth  = image.Width;
            _decalHeight = image.Height;
            _decalPixels = new Rgba32[image.Width * image.Height];
            image.CopyPixelDataTo(_decalPixels);
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not load decal image for the viewport: {ex.Message}");
        }
    }

    private void FrameCamera()
    {
        if (_mesh == null)
            return;

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var p in _mesh.Positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        _target   = (min + max) / 2f;
        _distance = MathF.Max(0.3f, (max - min).Length() * 0.9f);
        _yaw      = 0.3f;
        _pitch    = 0.1f;
    }

    /// <summary> Draw embedded in the current layout, or as a separate window when popped out. Call every frame. </summary>
    public void Draw(DTexture current)
    {
        if (!_open || _mesh == null || _dTexture == null)
            return;

        // The viewport belongs to one dTexture — close if the selection moved on.
        if (!ReferenceEquals(current, _dTexture))
        {
            Close();
            return;
        }

        if (_poppedOut)
        {
            ImGui.SetNextWindowSize(new Vector2(820, 900) * ImUtf8.GlobalScale, ImGuiCond.FirstUseEver);
            var open = true;
            if (ImGui.Begin("3D Preview###dtmDecalViewport", ref open))
                DrawContent();
            ImGui.End();
            if (!open)
                _poppedOut = false;
        }
        else
        {
            var avail  = ImGui.GetContentRegionAvail();
            var height = MathF.Max(340f * ImUtf8.GlobalScale, avail.Y);
            using var child = ImUtf8.Child("##viewportChild"u8, new Vector2(avail.X, height), true);
            if (child)
                DrawContent();
        }

        // Commit once per completed interaction even if the mouse left the canvas.
        if (_editDirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGui.IsAnyItemActive())
        {
            _editDirty = false;
            _onChanged?.Invoke();
        }
    }

    private void DrawContent()
    {
        if (_mesh == null)
            return;

        if (ImUtf8.SmallButton(_poppedOut ? "Embed"u8 : "Pop Out"u8))
            _poppedOut = !_poppedOut;
        ImUtf8.HoverTooltip("Move the 3D preview between the Decals tab and its own resizable window."u8);
        ImGui.SameLine();

        if (_layer != null)
            DrawPlacementControls(_layer);
        else
            ImUtf8.TextWrapped("Right-drag: orbit.  Middle-drag: pan.  Wheel: zoom.  Add or place a decal to stamp on the mesh."u8);

        var avail = ImGui.GetContentRegionAvail();
        var size  = MathF.Max(200f, MathF.Min(avail.X, avail.Y));

        if (_renderDirty || _wrap == null)
        {
            Render();
            _renderDirty = false;
        }

        var start = ImGui.GetCursorScreenPos();
        ImUtf8.InvisibleButton("##viewportCanvas"u8, new Vector2(size));
        if (_wrap != null)
            ImGui.GetWindowDrawList().AddImage(_wrap.Handle, start, start + new Vector2(size));

        HandleInput(start, size);
    }

    private void DrawPlacementControls(DecalLayer layer)
    {
        ImUtf8.TextWrapped("Left-drag: place/move decal.  Right-drag: orbit.  Middle-drag: pan.  Wheel: zoom, Ctrl+wheel: decal size, Shift+wheel: decal rotation."u8);

        var widthCm = layer.WorldWidth * 100f;
        ImGui.SetNextItemWidth(130 * ImUtf8.GlobalScale);
        if (ImUtf8.Slider("Width (cm)"u8, ref widthCm, "%.1f"u8, 1f, 100f))
        {
            layer.WorldWidth = widthCm / 100f;
            MarkEdited();
        }

        ImGui.SameLine();
        var heightCm = layer.WorldHeight * 100f;
        ImGui.SetNextItemWidth(130 * ImUtf8.GlobalScale);
        if (ImUtf8.Slider("Height (cm)"u8, ref heightCm, "%.1f"u8, 1f, 100f))
        {
            layer.WorldHeight = heightCm / 100f;
            MarkEdited();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(130 * ImUtf8.GlobalScale);
        var rotation = layer.RotationDeg;
        if (ImUtf8.Slider("Rotation"u8, ref rotation, "%.0f°"u8, -180f, 180f))
        {
            layer.RotationDeg = rotation;
            MarkEdited();
        }

        ImGui.SameLine();
        if (ImUtf8.Checkbox("Highlight"u8, ref _highlightDecal))
            _renderDirty = true;
        ImUtf8.HoverTooltip("Render the decal as a bright orange footprint instead of its real colors — easier to find on busy textures."u8);

        ImGui.SameLine();
        if (ImUtf8.SmallButton("Done"u8))
            EndPlacement();
        ImUtf8.HoverTooltip("Finish placing this decal and return the preview to view mode."u8);
    }

    private void MarkEdited()
    {
        _renderDirty = true;
        _editDirty   = true;
    }

    private void HandleInput(Vector2 start, float size)
    {
        if (_mesh == null)
            return;

        var hovered = ImGui.IsItemHovered();
        var io      = ImGui.GetIO();

        if (hovered && io.MouseWheel != 0f)
        {
            if (io.KeyCtrl && _layer != null)
            {
                var factor = 1f + io.MouseWheel * 0.1f;
                _layer.WorldWidth  = Math.Clamp(_layer.WorldWidth * factor, 0.01f, 2f);
                _layer.WorldHeight = Math.Clamp(_layer.WorldHeight * factor, 0.01f, 2f);
                MarkEdited();
            }
            else if (io.KeyShift && _layer != null)
            {
                var rotation = _layer.RotationDeg + io.MouseWheel * 5f;
                _layer.RotationDeg = rotation switch
                {
                    > 180f  => rotation - 360f,
                    < -180f => rotation + 360f,
                    _       => rotation,
                };
                MarkEdited();
            }
            else
            {
                _distance    = Math.Clamp(_distance * (1f - io.MouseWheel * 0.1f), 0.05f, 20f);
                _renderDirty = true;
            }
        }

        if (hovered && ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            var delta = io.MouseDelta;
            if (delta != Vector2.Zero)
            {
                _yaw   -= delta.X * 0.01f;
                _pitch  = Math.Clamp(_pitch + delta.Y * 0.01f, -1.5f, 1.5f);
                _renderDirty = true;
            }
        }

        if (hovered && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            var delta = io.MouseDelta;
            if (delta != Vector2.Zero)
            {
                var eyeOffset = CameraOffset();
                var forward   = Vector3.Normalize(-eyeOffset);
                var right     = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
                var up        = Vector3.Cross(right, forward);
                _target      += (-right * delta.X + up * delta.Y) * _distance * 0.0015f;
                _renderDirty  = true;
            }
        }

        if (hovered && _layer != null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var local = (ImGui.GetMousePos() - start) / size;
            if (local is { X: >= 0f and <= 1f, Y: >= 0f and <= 1f } && TryPick(local, out var position, out var normal, out var part))
            {
                _layer.AnchorX           = position.X;
                _layer.AnchorY           = position.Y;
                _layer.AnchorZ           = position.Z;
                _layer.NormalX           = normal.X;
                _layer.NormalY           = normal.Y;
                _layer.NormalZ           = normal.Z;
                _layer.SurfacePart       = part;
                _layer.SurfaceAttributes = _visibleAttributes;
                _layer.SurfaceShapes     = 0;
                MarkEdited();
            }
        }
    }

    private Vector3 CameraOffset()
        => new Vector3(
            MathF.Cos(_pitch) * MathF.Sin(_yaw),
            MathF.Sin(_pitch),
            MathF.Cos(_pitch) * MathF.Cos(_yaw)) * _distance;

    private Matrix4x4 ViewProjection()
    {
        var eye  = _target + CameraOffset();
        var view = Matrix4x4.CreateLookAt(eye, _target, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.7f, 1f, 0.01f, 50f);
        return view * proj;
    }

    private bool TryPick(Vector2 canvasUv, out Vector3 position, out Vector3 normal, out int part)
    {
        position = default;
        normal   = default;
        part     = -1;
        if (_mesh == null || !Matrix4x4.Invert(_lastViewProjection, out var inverse))
            return false;

        var ndc = new Vector2(canvasUv.X * 2f - 1f, 1f - canvasUv.Y * 2f);
        var np  = Unproject(new Vector3(ndc, 0.05f), inverse);
        var fp  = Unproject(new Vector3(ndc, 0.95f), inverse);
        if (np == null || fp == null)
            return false;

        var origin    = np.Value;
        var direction = Vector3.Normalize(fp.Value - np.Value);

        var best    = float.MaxValue;
        var bestTri = -1;
        var bary    = Vector3.Zero;
        for (var i = 0; i + 2 < _mesh.Indices.Length; i += 3)
        {
            if ((_mesh.TriangleAttributeMasks[i / 3] & ~_visibleAttributes) != 0)
                continue;

            if (!RayTriangle(origin, direction,
                    _mesh.Positions[_mesh.Indices[i]], _mesh.Positions[_mesh.Indices[i + 1]], _mesh.Positions[_mesh.Indices[i + 2]],
                    out var t, out var b) || t >= best)
                continue;

            best    = t;
            bestTri = i;
            bary    = b;
        }

        if (bestTri < 0)
            return false;

        var i0 = _mesh.Indices[bestTri];
        var i1 = _mesh.Indices[bestTri + 1];
        var i2 = _mesh.Indices[bestTri + 2];
        position = _mesh.Positions[i0] * bary.X + _mesh.Positions[i1] * bary.Y + _mesh.Positions[i2] * bary.Z;
        normal   = _mesh.Normals[i0] * bary.X + _mesh.Normals[i1] * bary.Y + _mesh.Normals[i2] * bary.Z;
        normal   = normal.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(normal);
        part     = _mesh.TriangleParts[bestTri / 3];
        return true;
    }

    private static Vector3? Unproject(Vector3 ndc, in Matrix4x4 inverseViewProjection)
    {
        var v = Vector4.Transform(new Vector4(ndc, 1f), inverseViewProjection);
        return MathF.Abs(v.W) < 1e-9f ? null : new Vector3(v.X, v.Y, v.Z) / v.W;
    }

    private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float t, out Vector3 bary)
    {
        t    = 0;
        bary = default;
        var edge1 = b - a;
        var edge2 = c - a;
        var p     = Vector3.Cross(direction, edge2);
        var det   = Vector3.Dot(edge1, p);
        if (MathF.Abs(det) < 1e-9f)
            return false;

        var invDet = 1f / det;
        var s      = origin - a;
        var u      = Vector3.Dot(s, p) * invDet;
        if (u is < 0f or > 1f)
            return false;

        var q = Vector3.Cross(s, edge1);
        var v = Vector3.Dot(direction, q) * invDet;
        if (v < 0f || u + v > 1f)
            return false;

        t = Vector3.Dot(edge2, q) * invDet;
        if (t <= 0.0001f)
            return false;

        bary = new Vector3(1f - u - v, u, v);
        return true;
    }

    /// <summary> The material's base color at a UV coordinate: diffuse sample times colorset row color. </summary>
    private static Vector3 SampleAlbedo(DecodedTexture? diffuse, DecodedTexture? idMap, Vector3[]? rows, Vector2 uv)
    {
        var albedo = Vector3.One;
        var shaded = false;
        if (diffuse != null)
        {
            var x = Math.Clamp((int)(uv.X * diffuse.Width), 0, diffuse.Width - 1);
            var y = Math.Clamp((int)(uv.Y * diffuse.Height), 0, diffuse.Height - 1);
            var i = (y * diffuse.Width + x) * 4;
            albedo *= new Vector3(diffuse.Rgba[i] / 255f, diffuse.Rgba[i + 1] / 255f, diffuse.Rgba[i + 2] / 255f);
            shaded  = true;
        }

        if (idMap != null && rows != null)
        {
            // Nearest texel: bilinear on the pair byte would blend unrelated pairs.
            var x = Math.Clamp((int)(uv.X * idMap.Width), 0, idMap.Width - 1);
            var y = Math.Clamp((int)(uv.Y * idMap.Height), 0, idMap.Height - 1);
            var i = (y * idMap.Width + x) * 4;
            albedo *= IdMapTexel.BlendedRowColor(rows, idMap.Rgba[i], idMap.Rgba[i + 1]);
            shaded  = true;
        }

        return shaded ? albedo : new Vector3(190f / 255f);
    }

    // Fixed-size render targets, reused across frames — a drag re-renders every frame and
    // fresh 2.3 MB + 2.3 MB arrays per frame would churn the large-object heap.
    private readonly byte[]  _renderRgba  = new byte[RenderSize * RenderSize * 4];
    private readonly float[] _renderDepth = new float[RenderSize * RenderSize];

    /// <summary> Software-render the mesh with the material's shading and the bound decal projected live. </summary>
    private void Render()
    {
        if (_mesh == null)
            return;

        const int size = RenderSize;
        var rgba  = _renderRgba;
        var depth = _renderDepth;
        Array.Fill(depth, float.MaxValue);
        for (var i = 0; i < size * size; ++i)
        {
            rgba[i * 4]     = 28;
            rgba[i * 4 + 1] = 30;
            rgba[i * 4 + 2] = 34;
            rgba[i * 4 + 3] = 255;
        }

        // Hoisted out of the per-pixel path — invariant for the whole render.
        var shadingDiffuse = _shading?.Diffuse;
        var shadingIdMap   = _shading?.IdMap;
        var shadingRows    = _shading?.RowDiffuse;

        var viewProjection = ViewProjection();
        _lastViewProjection = viewProjection;
        var eyeDirection = Vector3.Normalize(CameraOffset());

        var layer     = _layer;
        var anchored  = layer is not (null or { AnchorX: 0f, AnchorY: 0f, AnchorZ: 0f });
        var anchor    = layer == null ? Vector3.Zero : new Vector3(layer.AnchorX, layer.AnchorY, layer.AnchorZ);
        var normalDir = layer == null ? Vector3.UnitY : new Vector3(layer.NormalX, layer.NormalY, layer.NormalZ);
        var (tangent, bitangent) = normalDir.LengthSquared() > 1e-6f
            ? SurfaceDecalBaker.TangentFrame(Vector3.Normalize(normalDir), layer?.RotationDeg ?? 0f)
            : (Vector3.UnitX, Vector3.UnitZ);
        if (normalDir.LengthSquared() > 1e-6f)
            normalDir = Vector3.Normalize(normalDir);
        var maxDepth  = layer == null ? 0f : MathF.Max(layer.WorldWidth, layer.WorldHeight) * 0.4f;
        var threshold = layer?.AlphaThresholdByte ?? (byte)128;
        var rows      = _shading?.RowDiffuse;
        var realColor = !_highlightDecal && layer != null
         && (!layer.IdRemap || (rows != null && layer.PaletteRows.Count > 0 && layer.PaletteRows.Count == layer.PaletteColors.Count));

        Span<Vector2> screen = stackalloc Vector2[3];
        Span<float>   depths = stackalloc float[3];
        for (var i = 0; i + 2 < _mesh.Indices.Length; i += 3)
        {
            var triangle = i / 3;
            if ((_mesh.TriangleAttributeMasks[triangle] & ~_visibleAttributes) != 0)
                continue;

            var i0 = _mesh.Indices[i];
            var i1 = _mesh.Indices[i + 1];
            var i2 = _mesh.Indices[i + 2];
            var clipped = false;
            for (var k = 0; k < 3; ++k)
            {
                var p    = _mesh.Positions[k == 0 ? i0 : k == 1 ? i1 : i2];
                var clip = Vector4.Transform(new Vector4(p, 1f), viewProjection);
                if (clip.W <= 0.001f)
                {
                    clipped = true;
                    break;
                }

                var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
                screen[k] = new Vector2((ndc.X + 1f) / 2f * size, (1f - ndc.Y) / 2f * size);
                depths[k] = ndc.Z;
            }

            if (clipped)
                continue;

            var area = (screen[1].X - screen[0].X) * (screen[2].Y - screen[0].Y)
              - (screen[1].Y - screen[0].Y) * (screen[2].X - screen[0].X);
            if (MathF.Abs(area) < 1e-4f)
                continue;

            var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(screen[0].X, MathF.Min(screen[1].X, screen[2].X))));
            var maxX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(screen[0].X, MathF.Max(screen[1].X, screen[2].X))));
            var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(screen[0].Y, MathF.Min(screen[1].Y, screen[2].Y))));
            var maxY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(screen[0].Y, MathF.Max(screen[1].Y, screen[2].Y))));
            if (minX > maxX || minY > maxY)
                continue;

            var dimmed = layer is { SurfaceLimitToPart: true, SurfacePart: >= 0 } && _mesh.TriangleParts[triangle] != layer.SurfacePart;

            for (var y = minY; y <= maxY; ++y)
            {
                for (var x = minX; x <= maxX; ++x)
                {
                    var px = new Vector2(x + 0.5f, y + 0.5f);
                    var w0 = ((screen[1].X - px.X) * (screen[2].Y - px.Y) - (screen[1].Y - px.Y) * (screen[2].X - px.X)) / area;
                    var w1 = ((screen[2].X - px.X) * (screen[0].Y - px.Y) - (screen[2].Y - px.Y) * (screen[0].X - px.X)) / area;
                    var w2 = 1f - w0 - w1;
                    if (w0 < 0f || w1 < 0f || w2 < 0f)
                        continue;

                    var z     = depths[0] * w0 + depths[1] * w1 + depths[2] * w2;
                    var index = y * size + x;
                    if (z >= depth[index])
                        continue;

                    depth[index] = z;

                    var position = _mesh.Positions[i0] * w0 + _mesh.Positions[i1] * w1 + _mesh.Positions[i2] * w2;
                    var pixelNormal = _mesh.Normals[i0] * w0 + _mesh.Normals[i1] * w1 + _mesh.Normals[i2] * w2;
                    var light = pixelNormal.LengthSquared() > 1e-8f
                        ? 0.35f + 0.65f * MathF.Abs(Vector3.Dot(Vector3.Normalize(pixelNormal), eyeDirection))
                        : 0.6f;

                    var uv    = _mesh.Uvs[i0] * w0 + _mesh.Uvs[i1] * w1 + _mesh.Uvs[i2] * w2;
                    var color = SampleAlbedo(shadingDiffuse, shadingIdMap, shadingRows, uv) * 255f;
                    if (dimmed)
                        color *= 0.4f;

                    if (anchored && !dimmed && layer != null)
                    {
                        var d  = position - anchor;
                        var du = Vector3.Dot(d, tangent) / layer.WorldWidth + 0.5f;
                        var dv = Vector3.Dot(d, bitangent) / layer.WorldHeight + 0.5f;
                        var dz = Vector3.Dot(d, normalDir);
                        if (du is >= 0f and <= 1f && dv is >= 0f and <= 1f && MathF.Abs(dz) <= maxDepth)
                        {
                            var sample = _decalPixels == null
                                ? new Rgba32(255, 255, 255, 255)
                                : SurfaceDecalBaker.SampleBilinear(_decalPixels, _decalWidth, _decalHeight, du, dv);
                            if (layer.IdRemap)
                            {
                                if (sample.A >= threshold)
                                    color = realColor
                                        ? _shading!.RowDiffuse![layer.PaletteRows[DecalQuantizer.NearestIndex(sample, layer.PaletteColors)]] * 255f
                                        : new Vector3(255f, 140f, 0f);
                            }
                            else
                            {
                                var alpha = sample.A / 255f * Math.Clamp(layer.Opacity, 0f, 1f);
                                if (alpha > 0f)
                                    color = realColor
                                        ? Vector3.Lerp(color, new Vector3(sample.R, sample.G, sample.B), alpha)
                                        : new Vector3(255f, 140f, 0f);
                            }
                        }
                    }

                    color *= light;
                    rgba[index * 4]     = (byte)Math.Clamp((int)color.X, 0, 255);
                    rgba[index * 4 + 1] = (byte)Math.Clamp((int)color.Y, 0, 255);
                    rgba[index * 4 + 2] = (byte)Math.Clamp((int)color.Z, 0, 255);
                }
            }
        }

        _wrap?.Dispose();
        _wrap = textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(size, size), rgba, "DTM Viewport");
    }
}
