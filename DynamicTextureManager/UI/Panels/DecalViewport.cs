using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.ModGeneration;
using ImSharp;
using Luna;
using SixLabors.ImageSharp.PixelFormats;
// Both ImSharp and ImageSharp define an Rgba32; this file's pixel work is ImageSharp's.
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace DynamicTextureManager.UI.Panels;

/// <summary>
/// Everything the viewport samples to shade the mesh with the material's real look: the
/// composited diffuse, the composited id map and the 32 resolved colorset row diffuse
/// colors (the dTexture's edits applied). Any part may be null and falls back to gray.
/// For hair materials <paramref name="HairColors"/> is set and <paramref name="Diffuse"/>
/// carries the composited NORMAL map instead — hair has no diffuse; its blue channel blends
/// the main color toward the highlight color and its alpha is the card cutout.
/// <paramref name="HairMask"/> optionally carries the composited hair mask, whose alpha
/// (ambient occlusion) shades the strands — without it a light-colored hairstyle washes out
/// to a flat silhouette.
/// </summary>
public sealed record ViewportShading(DecodedTexture? Diffuse, DecodedTexture? IdMap, Vector3[]? RowDiffuse, Vector3? SkinTone = null,
    (Vector3 Main, Vector3 Highlight)? HairColors = null, DecodedTexture? HairMask = null, ViewportEffect? Effect = null);

/// <summary>
/// A live stand-in for the animated-effect conversion, following the shader-verified math:
/// uv = texcoord × tiling + time(seconds) × scroll, pattern sample squared (gamma) times
/// the emissive color — in the display domain that reduces to DisplayColor × sample.
/// DisplayColor is the root of the stored squared colorset color times intensity (the same
/// conversion the hair colors get). PatternRgba must be a CACHED array — the record
/// compares it by reference to decide whether the shading changed.
/// </summary>
public sealed record ViewportEffect(byte[] PatternRgba, int PatternSize, Vector3 DisplayColor,
    float ScrollU, float ScrollV, float TilingU, float TilingV, bool FullCoverage = false);

/// <summary>
/// An extra mesh rendered alongside the primary selected material — overlay parts (nails,
/// accents) sharing the same body model set, or the OTHER hair materials of the same hair
/// model (modded styles split their strands across several materials) — each painted with its
/// OWN composited texture, so the viewport shows the complete subject instead of the dimmed,
/// wrong-UV "context" look. Additive to the primary render path: never affects it.
/// </summary>
public sealed record ViewportOverlay(MaterialMesh Mesh, DecodedTexture? Diffuse, bool ApplySkinTone,
    (Vector3 Main, Vector3 Highlight)? HairColors = null, DecodedTexture? HairMask = null);

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

    private IReadOnlyList<ViewportOverlay> _overlays = [];

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
        var dTextureChanged = !ReferenceEquals(_dTexture, dTexture);
        var meshChanged     = !ReferenceEquals(_mesh, mesh);
        var firstMesh       = _mesh == null;
        _dTexture = dTexture;
        if (_visibleAttributes != visibleAttributes)
            _renderDirty = true;
        _visibleAttributes = visibleAttributes;

        if (dTextureChanged && _layer != null)
        {
            // A different project: the placement binding belongs to its layers, drop it.
            DynamicTextureManager.Log.Debug("Viewport placement unbound — the selected dTexture changed.");
            _layer     = null;
            _onChanged = null;
        }

        if (meshChanged)
        {
            // The mesh instance can turn over without a real subject change (the reader's
            // caches re-resolve while rebuilds reload the mod) — keep an active placement
            // and the camera then. A different SUBJECT (another model — e.g. switching from
            // the hair to the tail within one project) must re-frame, or the camera keeps
            // pointing at the previous piece and the viewport shows empty space.
            var subjectChanged = firstMesh
             || !string.Equals(_mesh!.GamePath, mesh.GamePath, StringComparison.OrdinalIgnoreCase);
            _mesh        = mesh;
            _renderDirty = true;
            if (dTextureChanged || subjectChanged)
            {
                FrameCamera();
                _lastInteractiveCost = 0;
            }
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
         && ReferenceEquals(_shading?.RowDiffuse, shading?.RowDiffuse)
         && Nullable.Equals(_shading?.SkinTone, shading?.SkinTone)
         && Nullable.Equals(_shading?.HairColors, shading?.HairColors)
         && ReferenceEquals(_shading?.HairMask, shading?.HairMask)
         && Equals(_shading?.Effect, shading?.Effect))
            return;

        _shading     = shading;
        _renderDirty = true;
    }

    /// <summary>
    /// Swap in the overlay-part entries (nails, accents); re-renders only when the set actually
    /// changed. Additive — never touches the primary mesh/shading, so it cannot regress the
    /// single-texture preview when there are no overlays (the common case: gear, non-skin
    /// materials, or skin materials with no overlay parts added).
    /// </summary>
    public void SetOverlays(IReadOnlyList<ViewportOverlay> overlays)
    {
        if (_overlays.Count == overlays.Count
         && _overlays.Zip(overlays, (a, b)
                => ReferenceEquals(a.Mesh, b.Mesh) && ReferenceEquals(a.Diffuse, b.Diffuse) && a.ApplySkinTone == b.ApplySkinTone
                 && Nullable.Equals(a.HairColors, b.HairColors) && ReferenceEquals(a.HairMask, b.HairMask))
            .All(same => same))
            return;

        _overlays    = overlays;
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
            Im.Window.SetNextSize(new Vector2(820, 900) * Im.Style.GlobalScale, Condition.FirstUseEver);
            var open = true;
            using (var window = Im.Window.Begin("3D Preview###dtmDecalViewport"u8, ref open))
            {
                if (window)
                    DrawContent();
            }

            if (!open)
                _poppedOut = false;
        }
        else
        {
            var avail  = Im.ContentRegion.Available;
            var height = MathF.Max(340f * Im.Style.GlobalScale, avail.Y);
            using var child = Im.Child.Begin("##viewportChild"u8, new Vector2(avail.X, height), true);
            if (child)
                DrawContent();
        }

        // Commit once per completed interaction even if the mouse left the canvas.
        if (_editDirty && !Im.Mouse.IsDown(MouseButton.Left) && !Im.Item.AnyActive)
        {
            _editDirty = false;
            _onChanged?.Invoke();
        }
    }

    private void DrawContent()
    {
        if (_mesh == null)
            return;

        if (Im.SmallButton(_poppedOut ? "Embed"u8 : "Pop Out"u8))
            _poppedOut = !_poppedOut;
        Im.Tooltip.OnHover("Move the 3D preview between the Decals tab and its own resizable window."u8);

        Im.Line.Same();
        if (Im.SmallButton("Reset View"u8))
        {
            FrameCamera();
            _renderDirty = true;
        }

        Im.Tooltip.OnHover("Re-frame the camera on the whole piece."u8);

        Im.Line.Same();
        Im.Text("(?)"u8);
        Im.Tooltip.OnHover(
            "Right-drag: orbit.  Middle-drag: pan.  Wheel: zoom.\nWhile placing a decal: left-drag places/moves it, Ctrl+wheel resizes it, Shift+wheel rotates it.\nThe colored corner cross shows the world axes (X red, Y green, Z blue); the live hints inside the canvas light up when a modifier is active."u8);

        if (_layer != null)
            DrawPlacementControls(_layer);

        var avail = Im.ContentRegion.Available;
        var size  = MathF.Max(200f, MathF.Min(avail.X, avail.Y));

        // Camera interaction degrades gracefully instead of re-rasterizing 768² every frame
        // (which tanked game fps): while orbiting/panning/zooming, render at reduced
        // resolution, paced by the previous render's own measured cost — a fixed 30fps
        // cadence let a dense modded hairstyle (high triangle count + card overdraw) eat
        // the whole frame budget. A final full-resolution render lands on release.
        var now         = Im.State.Time;
        var interacting = now - _lastCameraChange < 0.15;
        if (_renderDirty || _wrap == null)
        {
            var interval = Math.Max(1.0 / 30, _lastInteractiveCost * 3);
            if (_wrap == null || !interacting || now - _lastRenderTime >= interval)
            {
                Render(interacting ? RenderSize / 2 : RenderSize);
                _renderDirty    = false;
                _fullResPending = interacting;
            }
        }
        else if (_fullResPending && !interacting)
        {
            Render(RenderSize);
            _fullResPending = false;
        }
        else if (_shading?.Effect != null && _effectPixelCount > 0 && now - _lastEffectFrame >= 1.0 / 30)
        {
            // Animate the effect WITHOUT re-rasterizing: only the scrolling emissive is
            // re-composited over the cached base render.
            PresentFrame();
        }

        var start = Im.Cursor.ScreenPosition;
        Im.InvisibleButton("##viewportCanvas"u8, new Vector2(size));
        if (_wrap != null)
            Im.Window.DrawList.Image(_wrap.Id, start, start + new Vector2(size));

        DrawCanvasOverlays(start, size);
        HandleInput(start, size);
    }

    /// <summary>
    /// In-canvas overlays: live control hints that light up with the active modifier, and an
    /// orientation gizmo (world axes projected through the current camera) in the corner.
    /// </summary>
    private void DrawCanvasOverlays(Vector2 start, float size)
    {
        var draw = Im.Window.DrawList;

        // Control hints, top-left. The active modifier's line lights up so the current
        // wheel mode is always visible at a glance.
        const uint dimColor = 0xAAB4B4B4;
        const uint hotColor = 0xFF53D7FF;
        var keyControl = Im.Io.KeyControl;
        var keyShift   = Im.Io.KeyShift;
        Span<(string Text, uint Color)> lines = _layer != null
            ?
            [
                ("LMB place · RMB orbit · MMB pan · Wheel zoom", dimColor),
                (keyControl ? "Ctrl+Wheel: resizing decal" : "Ctrl+Wheel: resize decal", keyControl ? hotColor : dimColor),
                (keyShift ? "Shift+Wheel: rotating decal" : "Shift+Wheel: rotate decal", keyShift ? hotColor : dimColor),
            ]
            : [("RMB orbit · MMB pan · Wheel zoom", dimColor)];

        var pad       = 6f * Im.Style.GlobalScale;
        var lineStep  = Im.Style.TextHeight + 2f;
        var maxWidth  = 0f;
        foreach (var (text, _) in lines)
            maxWidth = MathF.Max(maxWidth, Im.Font.CalculateSize(text).X);
        var boxMin = start + new Vector2(pad, pad);
        draw.Shape.RectangleFilled(boxMin - new Vector2(4f), boxMin + new Vector2(maxWidth + 4f, lines.Length * lineStep + 2f), 0x90101010u,
            4f);
        for (var i = 0; i < lines.Length; ++i)
            draw.Text(boxMin + new Vector2(0f, i * lineStep), lines[i].Color, lines[i].Text);

        // Orientation gizmo, bottom-left: world axes through the camera's rotation. An axis
        // pointing away from the camera renders dimmed.
        var gizmoRadius = 20f * Im.Style.GlobalScale;
        var center      = start + new Vector2(gizmoRadius + 10f, size - gizmoRadius - 10f);
        var offset      = CameraOffset();
        var forward     = Vector3.Normalize(-offset);
        var right       = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up          = Vector3.Cross(right, forward);
        draw.Shape.CircleFilled(center, gizmoRadius + 8f, 0x60101010u);

        void Axis(Vector3 dir, uint color, string label)
        {
            var screen = new Vector2(Vector3.Dot(dir, right), -Vector3.Dot(dir, up));
            var away   = Vector3.Dot(dir, forward) > 0f;
            var col    = away ? (color & 0x00FFFFFFu) | 0x50000000u : color;
            var tip    = center + screen * gizmoRadius;
            draw.Shape.Line(center, tip, col, 2f);
            draw.Text(tip + screen * 3f - new Vector2(3.5f, 7f), col, label);
        }

        Axis(Vector3.UnitX, 0xFF4040E0, "X");
        Axis(Vector3.UnitY, 0xFF40C040, "Y");
        Axis(Vector3.UnitZ, 0xFFE07050, "Z");
    }

    private void DrawPlacementControls(DecalLayer layer)
    {
        var widthCm = layer.WorldWidth * 100f;
        Im.Item.SetNextWidthScaled(130);
        if (Im.Slider("Width (cm)"u8, ref widthCm, "%.1f"u8, 1f, 100f))
        {
            layer.WorldWidth = widthCm / 100f;
            MarkEdited();
        }

        Im.Line.Same();
        var heightCm = layer.WorldHeight * 100f;
        Im.Item.SetNextWidthScaled(130);
        if (Im.Slider("Height (cm)"u8, ref heightCm, "%.1f"u8, 1f, 100f))
        {
            layer.WorldHeight = heightCm / 100f;
            MarkEdited();
        }

        Im.Line.Same();
        Im.Item.SetNextWidthScaled(130);
        var rotation = layer.RotationDeg;
        if (Im.Slider("Rotation"u8, ref rotation, "%.0f°"u8, -180f, 180f))
        {
            layer.RotationDeg = rotation;
            MarkEdited();
        }

        Im.Line.Same();
        if (Im.SmallButton("Flip H"u8))
        {
            layer.FlipX = !layer.FlipX;
            MarkEdited();
        }

        Im.Tooltip.OnHover(layer.FlipX ? "Mirror the decal horizontally (currently flipped)."u8 : "Mirror the decal horizontally."u8);

        Im.Line.Same();
        if (Im.SmallButton("Flip V"u8))
        {
            layer.FlipY = !layer.FlipY;
            MarkEdited();
        }

        Im.Tooltip.OnHover(layer.FlipY ? "Mirror the decal vertically (currently flipped)."u8 : "Mirror the decal vertically."u8);

        Im.Line.Same();
        if (Im.Checkbox("Highlight"u8, ref _highlightDecal))
            _renderDirty = true;
        Im.Tooltip.OnHover("Render the decal as a bright orange footprint instead of its real colors — easier to find on busy textures."u8);

        Im.Line.Same();
        if (Im.SmallButton("Done"u8))
            EndPlacement();
        Im.Tooltip.OnHover("Finish placing this decal and return the preview to view mode."u8);
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

        var hovered = Im.Item.Hovered();
        var wheel   = Im.Io.MouseWheel;

        if (hovered && wheel != 0f)
        {
            if (Im.Io.KeyControl && _layer != null)
            {
                var factor = 1f + wheel * 0.1f;
                _layer.WorldWidth  = Math.Clamp(_layer.WorldWidth * factor, 0.01f, 2f);
                _layer.WorldHeight = Math.Clamp(_layer.WorldHeight * factor, 0.01f, 2f);
                MarkEdited();
            }
            else if (Im.Io.KeyShift && _layer != null)
            {
                var rotation = _layer.RotationDeg + wheel * 5f;
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
                _distance         = Math.Clamp(_distance * (1f - wheel * 0.1f), 0.05f, 20f);
                _renderDirty      = true;
                _lastCameraChange = Im.State.Time;
            }
        }

        // Per-frame drag deltas: the drag delta since the last reset stands in for the raw
        // per-frame mouse delta (ImSharp does not surface io.MouseDelta).
        if (hovered && Im.Mouse.IsDown(MouseButton.Right))
        {
            var delta = Im.Mouse.GetDragDelta(MouseButton.Right, 0f);
            if (delta != Vector2.Zero)
            {
                Im.Mouse.ResetDragDelta(MouseButton.Right);
                _yaw   -= delta.X * 0.01f;
                _pitch  = Math.Clamp(_pitch + delta.Y * 0.01f, -1.5f, 1.5f);
                _renderDirty      = true;
                _lastCameraChange = Im.State.Time;
            }
        }

        if (hovered && Im.Mouse.IsDown(MouseButton.Middle))
        {
            var delta = Im.Mouse.GetDragDelta(MouseButton.Middle, 0f);
            if (delta != Vector2.Zero)
            {
                Im.Mouse.ResetDragDelta(MouseButton.Middle);
                var eyeOffset = CameraOffset();
                var forward   = Vector3.Normalize(-eyeOffset);
                var right     = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
                var up        = Vector3.Cross(right, forward);
                _target          += (-right * delta.X + up * delta.Y) * _distance * 0.0015f;
                _renderDirty      = true;
                _lastCameraChange = Im.State.Time;
            }
        }

        // One diagnostic line per CLICK (not per drag frame): the first thing to check when
        // "clicking does nothing" — distinguishes a lost binding from a missed pick.
        if (hovered && Im.Mouse.IsClicked(MouseButton.Left))
        {
            var probe = (Im.Mouse.Position - start) / size;
            DynamicTextureManager.Log.Debug(_layer == null
                ? $"Viewport click at ({probe.X:F2}, {probe.Y:F2}) — no placement layer bound."
                : $"Viewport click at ({probe.X:F2}, {probe.Y:F2}) — pick {(TryPick(probe, out _, out _, out _) ? "hit" : "MISSED the mesh")}.");
        }

        if (hovered && _layer != null && Im.Mouse.IsDown(MouseButton.Left))
        {
            var local = (Im.Mouse.Position - start) / size;
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
            // Context geometry (other materials of the model set) cannot take decals.
            if (!_mesh.TriangleEditable[i / 3] || (_mesh.TriangleAttributeMasks[i / 3] & ~_visibleAttributes) != 0)
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

    /// <summary>
    /// The material's base color at a UV coordinate: diffuse sample times colorset row color.
    /// Materials without a colorset (skin, legacy diffuse) shade from the diffuse alone; skin
    /// additionally multiplies the preview skin tone weighted by the diffuse alpha — the
    /// stand-in for the customize skin color the game applies in-shader.
    /// </summary>
    private static Vector3 SampleAlbedo(DecodedTexture? diffuse, DecodedTexture? idMap, Vector3[]? rows, Vector3? skinTone,
        (Vector3 Main, Vector3 Highlight)? hairColors, DecodedTexture? hairMask, float[]? hairAoCurve, Vector2 uv)
    {
        var albedo = Vector3.One;
        var shaded = false;
        if (diffuse != null)
        {
            var x = Math.Clamp((int)(uv.X * diffuse.Width), 0, diffuse.Width - 1);
            var y = Math.Clamp((int)(uv.Y * diffuse.Height), 0, diffuse.Height - 1);
            var i = (y * diffuse.Width + x) * 4;
            if (hairColors is { } hair)
            {
                // Hair: the buffer is the composited NORMAL map. Its blue channel lerps the
                // customize main color toward the highlight color; the customize colors are
                // "squared RGB" in the game's constant buffer, so square them here too.
                albedo = Vector3.Lerp(hair.Main * hair.Main, hair.Highlight * hair.Highlight, diffuse.Rgba[i + 2] / 255f);
                if (hairMask != null && hairAoCurve != null)
                    albedo *= hairAoCurve[SampleAlpha(hairMask, uv)];
            }
            else
            {
                albedo *= new Vector3(diffuse.Rgba[i] / 255f, diffuse.Rgba[i + 1] / 255f, diffuse.Rgba[i + 2] / 255f);
                if (skinTone is { } tone)
                    albedo *= Vector3.Lerp(Vector3.One, tone, diffuse.Rgba[i + 3] / 255f);
            }

            shaded = true;
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

    // The BUILT companion mask multiplies the in-game diffuse by the strand AO NORMALIZED
    // around its own mean (AnimatedHairBuilder.BuildCharMaskRgba): a typical strand keeps
    // full brightness, only crevices darker than typical shade down. The preview must shade
    // with the SAME curve — multiplying by absolute AO muted the color across the whole
    // style (visibly less colored roots than in game). One 256-entry curve per mask
    // instance; the mask's alpha channel is the AO.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DecodedTexture, float[]> AoCurves = new();

    private static float[] HairAoCurve(DecodedTexture mask)
        => AoCurves.GetValue(mask, static m =>
        {
            long sum = 0;
            for (var i = 3; i < m.Rgba.Length; i += 4)
                sum += m.Rgba[i];
            var mean  = Math.Max(1f, sum / (m.Rgba.Length / 4f)) / 255f;
            var table = new float[256];
            for (var a = 0; a < 256; ++a)
            {
                var display = MathF.Pow(MathF.Min(1f, a / 255f / mean), 0.75f);
                table[a] = display * display;
            }

            return table;
        });

    private static byte SampleAlpha(DecodedTexture texture, Vector2 uv)
    {
        var x = Math.Clamp((int)(uv.X * texture.Width), 0, texture.Width - 1);
        var y = Math.Clamp((int)(uv.Y * texture.Height), 0, texture.Height - 1);
        return texture.Rgba[(y * texture.Width + x) * 4 + 3];
    }

    private static byte SampleBlue(DecodedTexture texture, Vector2 uv)
    {
        var x = Math.Clamp((int)(uv.X * texture.Width), 0, texture.Width - 1);
        var y = Math.Clamp((int)(uv.Y * texture.Height), 0, texture.Height - 1);
        return texture.Rgba[(y * texture.Width + x) * 4 + 2];
    }

    /// <summary>
    /// The effect pattern's brightness at a scrolled, stretched, wrapping UV — bilinear like
    /// the game's sampler, so the preview glow is as soft as the in-game one.
    /// </summary>
    private static float SampleEffect(ViewportEffect effect, Vector2 uv, Vector2 offset)
    {
        var size = effect.PatternSize;
        var u = (uv.X * effect.TilingU + offset.X) * size - 0.5f;
        var v = (uv.Y * effect.TilingV + offset.Y) * size - 0.5f;
        var x0 = (int)MathF.Floor(u);
        var y0 = (int)MathF.Floor(v);
        var fx = u - x0;
        var fy = v - y0;

        float At(int x, int y)
        {
            x = ((x % size) + size) % size;
            y = ((y % size) + size) % size;
            return effect.PatternRgba[(y * size + x) * 4];
        }

        var top    = At(x0, y0) * (1f - fx) + At(x0 + 1, y0) * fx;
        var bottom = At(x0, y0 + 1) * (1f - fx) + At(x0 + 1, y0 + 1) * fx;
        return (top * (1f - fy) + bottom * fy) / 255f;
    }

    // Fixed-size render targets, reused across frames — a drag re-renders every frame and
    // fresh 2.3 MB + 2.3 MB arrays per frame would churn the large-object heap.
    private readonly byte[]  _renderRgba  = new byte[RenderSize * RenderSize * 4];
    private readonly float[] _renderDepth = new float[RenderSize * RenderSize];

    // Marks pixels the primary mesh's OWN editable geometry has claimed this frame, so its
    // OWN dimmed/context geometry (which can sit almost exactly coincident with it — e.g. a
    // piercing accessory nearly touching the skin underneath) can never win the pixel back via
    // floating-point depth noise. See the pass-based split in RasterizeMesh.
    private readonly bool[] _editableTouched = new bool[RenderSize * RenderSize];

    /// <summary>
    /// Software-render the primary mesh (with the material's shading and the bound decal
    /// projected live) plus any bound overlay-part meshes (nails, accents — see
    /// <see cref="SetOverlays"/>), all into one shared framebuffer/depth-buffer so occlusion
    /// between them is correct. Overlays are strictly additive: with none bound, this renders
    /// byte-identical to the original single-mesh path.
    /// </summary>
    private void Render(int size = RenderSize)
    {
        if (_mesh == null)
            return;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _renderedSize = size;
        var rgba  = _renderRgba;
        var depth = _renderDepth;
        depth.AsSpan(0, size * size).Fill(float.MaxValue);
        Array.Clear(_editableTouched);
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

        var layer     = _layer;
        var anchored  = layer is not (null or { AnchorX: 0f, AnchorY: 0f, AnchorZ: 0f });
        var anchor    = layer == null ? Vector3.Zero : new Vector3(layer.AnchorX, layer.AnchorY, layer.AnchorZ);
        var normalDir = layer == null ? Vector3.UnitY : new Vector3(layer.NormalX, layer.NormalY, layer.NormalZ);
        var (tangent, bitangent) = normalDir.LengthSquared() > 1e-6f
            ? SurfaceDecalBaker.TangentFrame(Vector3.Normalize(normalDir), layer?.RotationDeg ?? 0f)
            : (Vector3.UnitX, Vector3.UnitZ);
        if (normalDir.LengthSquared() > 1e-6f)
            normalDir = Vector3.Normalize(normalDir);
        var threshold = layer?.AlphaThresholdByte ?? (byte)128;
        var rows      = _shading?.RowDiffuse;
        var realColor = !_highlightDecal && layer != null
         && (!layer.IdRemap || (rows != null && layer.PaletteRows.Count > 0 && layer.PaletteRows.Count == layer.PaletteColors.Count));
        var gradientPartners = layer is { IdRemap: true } ? DecalQuantizer.GradientPartners(layer) : [];

        // Animated effect: the rasterizer only RECORDS which pixels are highlight areas (and
        // their UV); the scrolling emissive itself is composited per animation frame in
        // PresentFrame so the mesh never re-rasterizes for animation.
        var effect = _shading?.Effect;
        if (effect != null)
        {
            // Full capacity regardless of the current render size — a half-res interactive
            // render must not shrink the buffers a later full-res render needs.
            _effectBlend ??= new float[RenderSize * RenderSize];
            _effectUv    ??= new Vector2[RenderSize * RenderSize];
            Array.Clear(_effectBlend);
        }

        // Renders one mesh into the shared framebuffer/depth-buffer. `skipContext` is true for
        // overlay entries: their OWN merged mesh includes the whole body as dimmed context
        // (same as the primary), which would duplicate-render it — only their own editable
        // (real) geometry is new here, the body itself already came from the primary pass.
        void RasterizeMesh(MaterialMesh mesh, DecodedTexture? meshDiffuse, DecodedTexture? meshIdMap, Vector3? meshSkinTone,
            (Vector3 Main, Vector3 Highlight)? meshHairColors, DecodedTexture? meshHairMask, bool skipContext)
        {
            // Hair renders as alpha-tested cutout cards: fully transparent texels of the hair
            // normal's alpha must not write depth or color at all, or the empty regions of a
            // card would occlude the cards behind it. Only editable geometry can be tested —
            // dimmed context belongs to a foreign material whose texture was never loaded.
            var alphaTest = meshHairColors != null && meshDiffuse != null;
            var hairAoCurve = meshHairColors != null && meshHairMask != null ? HairAoCurve(meshHairMask) : null;
            // Curvature-following per-vertex decal-space coordinates — the same computation the
            // bake uses (SurfaceDecalBaker.ComputeSurfaceProjection), so the live preview always
            // matches the built texture, including how it wraps/warps on curved surfaces, and
            // now also how it continues onto this specific mesh (the companion-bake reprojection).
            SurfaceDecalBaker.SurfaceProjection? projection = null;
            if (anchored && layer != null)
            {
                var walkRadius = SurfaceDecalBaker.WalkRadius(layer.WorldWidth, layer.WorldHeight);
                projection = SurfaceDecalBaker.ComputeSurfaceProjection(mesh, anchor, normalDir, tangent, bitangent, walkRadius);
            }

            Span<Vector2> screen = stackalloc Vector2[3];
            Span<float>   depths = stackalloc float[3];

            // Two passes for the primary mesh only (skipContext:false): editable geometry
            // first, so every pixel it touches is recorded in _editableTouched and can never
            // be reclaimed by this SAME mesh's own dimmed/context geometry afterwards — the
            // two frequently sit almost exactly coincident (e.g. a piercing accessory nearly
            // touching the skin underneath), and a plain depth test lets floating-point noise
            // decide the winner per-pixel, flickering a decal into fragments wherever context
            // happens to win. Overlay calls (skipContext:true) already exclude dimmed
            // triangles entirely at the top of the loop, so one pass suffices and they compete
            // normally (real depth test) against whatever the primary already drew.
            var passCount = skipContext ? 1 : 2;
            for (var pass = 0; pass < passCount; ++pass)
            {
                var wantDimmed = pass == 1;

                for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
                {
                    var triangle = i / 3;
                    if (skipContext && !mesh.TriangleEditable[triangle])
                        continue;
                    if ((mesh.TriangleAttributeMasks[triangle] & ~_visibleAttributes) != 0)
                        continue;

                    // Context geometry (other materials of the model set) renders dimmed for
                    // orientation — it shows the whole body/model, but decals cannot land on it.
                    var dimmed = !mesh.TriangleEditable[triangle]
                     || (layer is { SurfaceLimitToPart: true, SurfacePart: >= 0 } && mesh.TriangleParts[triangle] != layer.SurfacePart);
                    if (!skipContext && dimmed != wantDimmed)
                        continue;

                    var i0 = mesh.Indices[i];
                    var i1 = mesh.Indices[i + 1];
                    var i2 = mesh.Indices[i + 2];
                    var clipped = false;
                    for (var k = 0; k < 3; ++k)
                    {
                        var p    = mesh.Positions[k == 0 ? i0 : k == 1 ? i1 : i2];
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

                    // Curvature-following projection reached all three corners — same gate the bake
                    // uses; triangles outside the walk radius never receive the decal.
                    var triProjected = !dimmed && anchored && layer != null && projection != null
                     && projection.Reached[i0] && projection.Reached[i1] && projection.Reached[i2];
                    var proj0 = triProjected ? projection!.Local[i0] : default;
                    var proj1 = triProjected ? projection!.Local[i1] : default;
                    var proj2 = triProjected ? projection!.Local[i2] : default;

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

                            // Soft card edges render hard and true transparency sorting is out
                            // of scope — the same trade-off as the game's own cutout pass.
                            if (alphaTest && !dimmed)
                            {
                                var cutoutUv = mesh.Uvs[i0] * w0 + mesh.Uvs[i1] * w1 + mesh.Uvs[i2] * w2;
                                if (SampleAlpha(meshDiffuse!, cutoutUv) < 96)
                                    continue;
                            }

                            if (!skipContext && wantDimmed)
                            {
                                // Dimmed pass: a pixel the editable pass already claimed is
                                // permanently off-limits (even if this dimmed triangle's true
                                // depth is marginally closer — that's exactly the z-fighting
                                // this split exists to avoid), otherwise compete normally
                                // against whatever else was already drawn this pass.
                                if (_editableTouched[index] || z >= depth[index])
                                    continue;
                            }
                            else
                            {
                                if (z >= depth[index])
                                    continue;
                            }

                            depth[index] = z;
                            if (!skipContext && !wantDimmed)
                                _editableTouched[index] = true;

                            var pixelNormal = mesh.Normals[i0] * w0 + mesh.Normals[i1] * w1 + mesh.Normals[i2] * w2;
                            var facing = pixelNormal.LengthSquared() > 1e-8f
                                ? MathF.Abs(Vector3.Dot(Vector3.Normalize(pixelNormal), eyeDirection))
                                : 0.4f;
                            var light = 0.35f + 0.65f * facing;

                            // Dimmed/context geometry belongs to a DIFFERENT material than the one
                            // shaded here — its UVs point into a texture this pass never loaded, so
                            // sampling it would read essentially random texels (the "wrong-looking
                            // patch of mesh/color" artifact, e.g. a piercing's tiny jewelry mesh
                            // reading garbage from the body's own diffuse). It's shown only for
                            // orientation, so use a flat neutral gray instead of sampling at all.
                            Vector3 color;
                            var uv = Vector2.Zero;
                            if (dimmed)
                            {
                                color = new Vector3(190f) * 0.4f;
                            }
                            else
                            {
                                uv    = mesh.Uvs[i0] * w0 + mesh.Uvs[i1] * w1 + mesh.Uvs[i2] * w2;
                                color = SampleAlbedo(meshDiffuse, meshIdMap, rows, meshSkinTone, meshHairColors, meshHairMask, hairAoCurve, uv) * 255f;
                            }

                            if (triProjected)
                            {
                                var local = proj0 * w0 + proj1 * w1 + proj2 * w2;
                                var du    = local.X / layer!.WorldWidth + 0.5f;
                                var dv    = local.Y / layer.WorldHeight + 0.5f;
                                if (du is >= 0f and <= 1f && dv is >= 0f and <= 1f)
                                {
                                    // The bake flips the source image itself; sampling mirrored here matches it.
                                    var su = layer.FlipX ? 1f - du : du;
                                    var sv = layer.FlipY ? 1f - dv : dv;
                                    var sample = _decalPixels == null
                                        ? new Rgba32(255, 255, 255, 255)
                                        : SurfaceDecalBaker.SampleBilinear(_decalPixels, _decalWidth, _decalHeight, su, sv);
                                    if (layer.IdRemap)
                                    {
                                        if (sample.A >= threshold)
                                        {
                                            if (!realColor)
                                            {
                                                color = new Vector3(255f, 140f, 0f);
                                            }
                                            else
                                            {
                                                // Mirror the bake: gradient pairs blend the two
                                                // halves' row colors by the pixel's own G.
                                                var palIdx = DecalQuantizer.NearestIndex(sample, layer.PaletteColors);
                                                var row    = layer.PaletteRows[palIdx];
                                                if (gradientPartners[palIdx] >= 0)
                                                {
                                                    var aIndex = row % 2 == 0 ? palIdx : gradientPartners[palIdx];
                                                    var bIndex = row % 2 == 0 ? gradientPartners[palIdx] : palIdx;
                                                    var blend  = DecalQuantizer.GradientG(sample,
                                                        layer.PaletteColors[aIndex], layer.PaletteColors[bIndex]) / 255f;
                                                    var pair = row / 2;
                                                    color = Vector3.Lerp(_shading!.RowDiffuse![pair * 2 + 1],
                                                        _shading.RowDiffuse[pair * 2], blend) * 255f;
                                                }
                                                else
                                                {
                                                    color = _shading!.RowDiffuse![row] * 255f;
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        sample = DecalQuantizer.ApplyTint(sample, layer);
                                        var alpha = sample.A / 255f * Math.Clamp(layer.Opacity, 0f, 1f);
                                        if (alpha > 0f)
                                        {
                                            // Baked ink gets tinted by the skin color in-game like the
                                            // rest of the diffuse — preview it the same way.
                                            var decalColor = new Vector3(sample.R, sample.G, sample.B);
                                            if (meshSkinTone is { } tone)
                                                decalColor *= tone;
                                            color = realColor ? Vector3.Lerp(color, decalColor, alpha) : new Vector3(255f, 140f, 0f);
                                        }
                                    }
                                }
                            }

                            color *= light;

                            // Cheap view-based specular from the hair mask (R = spec power,
                            // G = roughness), so the Shine sliders visibly respond in the
                            // preview instead of only after an in-game Build.
                            if (!dimmed && meshHairColors != null && meshHairMask != null)
                            {
                                var mx = Math.Clamp((int)(uv.X * meshHairMask.Width), 0, meshHairMask.Width - 1);
                                var my = Math.Clamp((int)(uv.Y * meshHairMask.Height), 0, meshHairMask.Height - 1);
                                var mo = (my * meshHairMask.Width + mx) * 4;
                                var glossiness = 1f - meshHairMask.Rgba[mo + 1] / 255f;
                                var spec = MathF.Pow(facing, 6f + glossiness * 58f)
                                    * (meshHairMask.Rgba[mo] / 255f) * 0.45f;
                                color += new Vector3(230f) * spec;
                            }

                            // Highlight-blend (composited normal B) of the pixel's final
                            // surface, for the per-frame emissive pass. Every pixel write
                            // updates it, so occlusion between meshes stays correct.
                            if (effect != null)
                            {
                                // Full coverage (tails — no highlight channel): every visible
                                // texel of the piece carries the effect, matching the id map.
                                var blend = !dimmed && meshHairColors != null && meshDiffuse != null
                                    ? effect.FullCoverage ? 1f : SampleBlue(meshDiffuse, uv) / 255f
                                    : 0f;
                                _effectBlend![index] = blend;
                                if (blend > 0.01f)
                                    _effectUv![index] = uv;
                            }

                            rgba[index * 4]     = (byte)Math.Clamp((int)color.X, 0, 255);
                            rgba[index * 4 + 1] = (byte)Math.Clamp((int)color.Y, 0, 255);
                            rgba[index * 4 + 2] = (byte)Math.Clamp((int)color.Z, 0, 255);
                        }
                    }
                }
            }
        }

        RasterizeMesh(_mesh, _shading?.Diffuse, _shading?.IdMap, _shading?.SkinTone, _shading?.HairColors, _shading?.HairMask,
            skipContext: false);
        foreach (var overlay in _overlays)
            RasterizeMesh(overlay.Mesh, overlay.Diffuse, null, overlay.ApplySkinTone ? _shading?.SkinTone : null,
                overlay.HairColors, overlay.HairMask, skipContext: true);

        CollectEffectPixels();
        PresentFrame();

        if (size < RenderSize)
            _lastInteractiveCost = stopwatch.Elapsed.TotalSeconds;
    }

    // Interactive-degradation state: camera drags render half-resolution, paced by the
    // previous render's measured cost — a heavy mesh gets fewer preview updates per second
    // instead of a blockier image or a tanked game framerate. A final full-resolution
    // render lands after release.
    private int    _renderedSize = RenderSize;
    private double _lastRenderTime;
    private double _lastCameraChange;
    private bool   _fullResPending;
    private double _lastInteractiveCost;

    // Animated-effect compositing state: the base render is rasterized once; each animation
    // frame only re-adds the scrolling emissive over the recorded highlight pixels.
    private readonly byte[] _animRgba = new byte[RenderSize * RenderSize * 4];
    private float[]?   _effectBlend;
    private Vector2[]? _effectUv;
    private int[]      _effectPixels = [];
    private int        _effectPixelCount;
    private double     _lastEffectFrame;

    /// <summary> Compact the highlight-blend buffer into the pixel list the emissive pass iterates. </summary>
    private void CollectEffectPixels()
    {
        _effectPixelCount = 0;
        if (_shading?.Effect == null || _effectBlend == null)
            return;

        if (_effectPixels.Length < _effectBlend.Length)
            _effectPixels = new int[_effectBlend.Length];
        var count = _renderedSize * _renderedSize;
        for (var i = 0; i < count; ++i)
            if (_effectBlend[i] > 0.01f)
                _effectPixels[_effectPixelCount++] = i;
    }

    /// <summary>
    /// Publish the frame: the cached base render, plus — for an active animated effect — the
    /// scrolling emissive composited over the recorded highlight pixels only (unlit, like
    /// in-game emissive; the timebase approximates the in-game scroll rate).
    /// </summary>
    private void PresentFrame()
    {
        var effect = _shading?.Effect;
        var buffer = _renderRgba;
        if (effect != null && _effectPixelCount > 0)
        {
            Array.Copy(_renderRgba, _animRgba, _renderedSize * _renderedSize * 4);
            // Shader-verified: scroll = time in SECONDS × the scroll constants, no hidden
            // factor. The squared pattern sample times the linear emissive color reduces to
            // a LINEAR sample response in the display domain.
            var t      = (float)Im.State.Time;
            var offset = new Vector2(t * effect.ScrollU, t * effect.ScrollV);
            for (var n = 0; n < _effectPixelCount; ++n)
            {
                var index  = _effectPixels[n];
                var sample = SampleEffect(effect, _effectUv![index], offset);
                if (sample <= 0f)
                    continue;

                var add = effect.DisplayColor * (255f * _effectBlend![index] * sample);
                var o   = index * 4;
                _animRgba[o]     = (byte)Math.Clamp(_animRgba[o] + (int)add.X, 0, 255);
                _animRgba[o + 1] = (byte)Math.Clamp(_animRgba[o + 1] + (int)add.Y, 0, 255);
                _animRgba[o + 2] = (byte)Math.Clamp(_animRgba[o + 2] + (int)add.Z, 0, 255);
            }

            buffer = _animRgba;
        }

        _wrap?.Dispose();
        _wrap = textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(_renderedSize, _renderedSize), buffer, "DTM Viewport");
        _lastEffectFrame = Im.State.Time;
        _lastRenderTime  = _lastEffectFrame;
    }
}
