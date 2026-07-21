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

namespace DynamicTextureManager.UI.Panels;

/// <summary>
/// A 3D viewport window for placing surface decals: the gear mesh is software-rendered in
/// its bind pose — the exact space the bake works in — so picking is exact by construction,
/// independent of the live character's pose, race or body modifications. Left-drag stamps
/// and moves the decal (rendered live on the mesh), right-drag orbits, middle-drag pans,
/// wheel zooms, Ctrl+wheel resizes the decal and Shift+wheel rotates it.
/// </summary>
public sealed class DecalViewport(ITextureProvider textureProvider) : IDisposable
{
    private const int RenderSize = 768;

    private bool          _open;
    private DTexture?     _dTexture;
    private DecalLayer?   _layer;
    private MaterialMesh? _mesh;
    private uint          _visibleAttributes = uint.MaxValue;
    private Action?       _onChanged;

    private byte[]? _decalRgba;
    private int     _decalWidth;
    private int     _decalHeight;

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

    public void Open(DTexture dTexture, DecalLayer layer, MaterialMesh mesh, uint visibleAttributes, string decalPath, Action onChanged)
    {
        _dTexture          = dTexture;
        _layer             = layer;
        _mesh              = mesh;
        _visibleAttributes = visibleAttributes;
        _onChanged         = onChanged;
        _open              = true;
        _renderDirty       = true;

        LoadDecal(decalPath);
        FrameCamera();
    }

    public void Close()
        => _open = false;

    private void LoadDecal(string path)
    {
        _decalRgba = null;
        try
        {
            if (!File.Exists(path))
                return;

            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(path);
            _decalWidth  = image.Width;
            _decalHeight = image.Height;
            _decalRgba   = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(_decalRgba);
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

    /// <summary> Draw the window; call every frame. Closes itself when the layer disappears. </summary>
    public void Draw(DTexture current)
    {
        if (!_open || _layer == null || _mesh == null || _dTexture == null)
            return;

        // The viewport belongs to one dTexture and one layer — close if the selection moved on.
        if (!ReferenceEquals(current, _dTexture))
        {
            _open = false;
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(820, 900) * ImUtf8.GlobalScale, ImGuiCond.FirstUseEver);
        var open = _open;
        if (ImGui.Begin("Decal Placement — 3D View###dtmDecalViewport", ref open))
            DrawContent();
        ImGui.End();
        _open = open;

        if (!_open && _editDirty)
        {
            _editDirty = false;
            _onChanged?.Invoke();
        }
    }

    private void DrawContent()
    {
        if (_layer == null || _mesh == null)
            return;

        ImUtf8.Text("Left-drag: place/move decal.  Right-drag: orbit.  Middle-drag: pan.  Wheel: zoom, Ctrl+wheel: decal size, Shift+wheel: decal rotation."u8);

        var widthCm = _layer.WorldWidth * 100f;
        ImGui.SetNextItemWidth(150 * ImUtf8.GlobalScale);
        if (ImUtf8.Slider("Width (cm)"u8, ref widthCm, "%.1f"u8, 1f, 100f))
        {
            _layer.WorldWidth = widthCm / 100f;
            MarkEdited();
        }

        ImGui.SameLine();
        var heightCm = _layer.WorldHeight * 100f;
        ImGui.SetNextItemWidth(150 * ImUtf8.GlobalScale);
        if (ImUtf8.Slider("Height (cm)"u8, ref heightCm, "%.1f"u8, 1f, 100f))
        {
            _layer.WorldHeight = heightCm / 100f;
            MarkEdited();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150 * ImUtf8.GlobalScale);
        var rotation = _layer.RotationDeg;
        if (ImUtf8.Slider("Rotation"u8, ref rotation, "%.0f°"u8, -180f, 180f))
        {
            _layer.RotationDeg = rotation;
            MarkEdited();
        }

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

    private void MarkEdited()
    {
        _renderDirty = true;
        _editDirty   = true;
    }

    private void HandleInput(Vector2 start, float size)
    {
        if (_layer == null || _mesh == null)
            return;

        var hovered = ImGui.IsItemHovered();
        var io      = ImGui.GetIO();

        if (hovered && io.MouseWheel != 0f)
        {
            if (io.KeyCtrl)
            {
                var factor = 1f + io.MouseWheel * 0.1f;
                _layer.WorldWidth  = Math.Clamp(_layer.WorldWidth * factor, 0.01f, 2f);
                _layer.WorldHeight = Math.Clamp(_layer.WorldHeight * factor, 0.01f, 2f);
                MarkEdited();
            }
            else if (io.KeyShift)
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

        if (hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left))
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

        // Commit once per completed interaction, not per frame.
        if (_editDirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left) && io.MouseWheel == 0f && !ImGui.IsAnyItemActive())
        {
            _editDirty = false;
            _onChanged?.Invoke();
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

    /// <summary> Software-render the mesh with the decal projected live, into a texture wrap. </summary>
    private void Render()
    {
        if (_mesh == null || _layer == null)
            return;

        const int size = RenderSize;
        var rgba  = new byte[size * size * 4];
        var depth = new float[size * size];
        Array.Fill(depth, float.MaxValue);
        for (var i = 0; i < size * size; ++i)
        {
            rgba[i * 4]     = 28;
            rgba[i * 4 + 1] = 30;
            rgba[i * 4 + 2] = 34;
            rgba[i * 4 + 3] = 255;
        }

        var viewProjection = ViewProjection();
        _lastViewProjection = viewProjection;
        var eyeDirection = Vector3.Normalize(CameraOffset());

        var anchored  = _layer is not { AnchorX: 0f, AnchorY: 0f, AnchorZ: 0f };
        var anchor    = new Vector3(_layer.AnchorX, _layer.AnchorY, _layer.AnchorZ);
        var normalDir = new Vector3(_layer.NormalX, _layer.NormalY, _layer.NormalZ);
        var (tangent, bitangent) = normalDir.LengthSquared() > 1e-6f
            ? SurfaceDecalBaker.TangentFrame(Vector3.Normalize(normalDir), _layer.RotationDeg)
            : (Vector3.UnitX, Vector3.UnitZ);
        if (normalDir.LengthSquared() > 1e-6f)
            normalDir = Vector3.Normalize(normalDir);
        var maxDepth  = MathF.Max(_layer.WorldWidth, _layer.WorldHeight) * 0.4f;
        var threshold = (byte)Math.Clamp((int)Math.Round(_layer.AlphaThreshold * 255f), 1, 255);

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

            var dimmed = _layer.SurfaceLimitToPart && _layer.SurfacePart >= 0 && _mesh.TriangleParts[triangle] != _layer.SurfacePart;

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

                    var baseGray = dimmed ? 90f : 190f;
                    var r = baseGray * light;
                    var g = baseGray * light;
                    var b = baseGray * light;

                    if (anchored && !dimmed)
                    {
                        var d  = position - anchor;
                        var du = Vector3.Dot(d, tangent) / _layer.WorldWidth + 0.5f;
                        var dv = Vector3.Dot(d, bitangent) / _layer.WorldHeight + 0.5f;
                        var dz = Vector3.Dot(d, normalDir);
                        if (du is >= 0f and <= 1f && dv is >= 0f and <= 1f && MathF.Abs(dz) <= maxDepth
                         && SampleDecalAlpha(du, dv) >= threshold)
                        {
                            r = 255f * light;
                            g = 140f * light;
                            b = 0f;
                        }
                    }

                    rgba[index * 4]     = (byte)Math.Clamp((int)r, 0, 255);
                    rgba[index * 4 + 1] = (byte)Math.Clamp((int)g, 0, 255);
                    rgba[index * 4 + 2] = (byte)Math.Clamp((int)b, 0, 255);
                }
            }
        }

        _wrap?.Dispose();
        _wrap = textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(size, size), rgba, "DTM Viewport");
    }

    private byte SampleDecalAlpha(float u, float v)
    {
        if (_decalRgba == null || _decalWidth == 0 || _decalHeight == 0)
            return 255;

        var x = Math.Clamp((int)(u * (_decalWidth - 1)), 0, _decalWidth - 1);
        var y = Math.Clamp((int)(v * (_decalHeight - 1)), 0, _decalHeight - 1);
        return _decalRgba[(y * _decalWidth + x) * 4 + 3];
    }
}
