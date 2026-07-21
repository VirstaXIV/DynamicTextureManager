using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.ModGeneration;
using DynamicTextureManager.ModGeneration.Shaders;
using DynamicTextureManager.Services;
using OtterGui.Raii;
using OtterGui.Services;
using OtterGui.Text;

namespace DynamicTextureManager.UI.Panels;

/// <summary>
/// View-only tab showing the generated textures of the selected source materials, like
/// Penumbra's texture view: pick a material, click a texture thumbnail, zoom and pan the
/// full composited result. No editing here — decals are edited in the Decals tab and their
/// built pixels are what this tab displays.
/// </summary>
public sealed class TextureViewerTab(
    SourceFileProvider sourceFiles,
    ShaderHandlerRegistry shaderHandlers,
    CompositePreviewCache previewCache,
    ModelUvReader uvReader,
    ITextureProvider textureProvider,
    Configuration config)
    : IService, IDisposable
{
    private Guid                 _cacheOwner = Guid.Empty;
    private string               _sourceFingerprint = string.Empty;
    private List<TextureOption>? _options;
    private string               _selectedMaterial = string.Empty;
    private string               _selectedTexture = string.Empty;

    private bool    _showComposited = true;
    private bool    _colorizeIdMap = true;
    private float   _zoom = 1f;
    private Vector2 _pan = Vector2.Zero;

    private IDalamudTextureWrap? _colorizedWrap;
    private (string Path, bool Composited, int EntryVersion) _colorizedKey = (string.Empty, false, -1);
    private Vector3[]?           _colorizedRows;

    public void Dispose()
        => _colorizedWrap?.Dispose();

    public void Draw(DTexture dTexture)
    {
        var fingerprint = string.Join('\n', dTexture.Data.Source.Materials.Select(m => m.GamePath));
        if (_cacheOwner != dTexture.Identifier || _sourceFingerprint != fingerprint)
        {
            _cacheOwner        = dTexture.Identifier;
            _sourceFingerprint = fingerprint;
            _options           = null;
            _selectedMaterial  = string.Empty;
            _selectedTexture   = string.Empty;
            ResetView();
        }

        if (dTexture.Data.Source.IsEmpty)
        {
            ImUtf8.Text("Select a source first."u8);
            return;
        }

        _options ??= TextureOptions.Collect(dTexture.Data, sourceFiles, shaderHandlers);
        if (_options.Count == 0)
        {
            ImUtf8.Text("The source materials expose no textures."u8);
            return;
        }

        ImGui.SetNextItemWidth(350 * ImUtf8.GlobalScale);
        if (TextureOptions.DrawMaterialCombo(_options, ref _selectedMaterial))
        {
            _selectedTexture = string.Empty;
            ResetView();
        }

        ImGui.SameLine();
        ImUtf8.Text("Material"u8);

        var options = _options
            .Where(o => string.Equals(o.MaterialGamePath, _selectedMaterial, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (options.Count == 0)
        {
            ImUtf8.Text("This material exposes no textures."u8);
            return;
        }

        if (options.All(o => !string.Equals(o.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedTexture = (options.Find(o => o.DecalRecommended) ?? options[0]).GamePath;
            ResetView();
        }

        DrawThumbnails(dTexture, options);

        var selected = options.Find(o => string.Equals(o.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase));
        if (selected == null)
            return;

        ImGui.Separator();
        DrawMainView(dTexture, selected);
    }

    private void ResetView()
    {
        _zoom = 1f;
        _pan  = Vector2.Zero;
    }

    /// <summary> One clickable thumbnail per texture of the material, labeled by slot. </summary>
    private void DrawThumbnails(DTexture dTexture, List<TextureOption> options)
    {
        var thumbSize = new Vector2(96 * ImUtf8.GlobalScale);
        foreach (var (option, idx) in options.Select((o, i) => (o, i)))
        {
            using var id = ImUtf8.PushId(idx);
            if (idx > 0)
                ImGui.SameLine();

            using var group = ImUtf8.Group();
            var entry = previewCache.Get(dTexture, option.GamePath);
            var wrap  = DisplayWrap(entry);

            var selected = string.Equals(option.GamePath, _selectedTexture, StringComparison.OrdinalIgnoreCase);
            using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), selected))
            {
                var clicked = wrap != null
                    ? ImGui.ImageButton(wrap.Handle, thumbSize)
                    : ImUtf8.Button("...##thumb"u8, thumbSize);
                if (clicked && !selected)
                {
                    _selectedTexture = option.GamePath;
                    ResetView();
                }
            }

            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip($"{TextureOptions.SlotLabel(option)}\n{option.GamePath}");

            ImUtf8.Text(TextureOptions.SlotLabel(option));
        }
    }

    private void DrawMainView(DTexture dTexture, TextureOption option)
    {
        var entry = previewCache.Get(dTexture, option.GamePath);
        if (entry.Pristine == null)
        {
            ImUtf8.Text(entry.Building ? "Loading texture..."u8 : "No preview available for this texture."u8);
            return;
        }

        if (ImGui.RadioButton("Source", !_showComposited))
            _showComposited = false;
        ImUtf8.HoverTooltip("The pristine texture the build starts from — vanilla or the source mod's file."u8);
        ImGui.SameLine();
        if (ImGui.RadioButton("Generated", _showComposited))
            _showComposited = true;
        ImUtf8.HoverTooltip("The composited result the build writes: all decal layers and material effects applied."u8);

        ImGui.SameLine();
        var showSeams = config.ShowUvSeams;
        if (ImUtf8.Checkbox("Show UV Seams"u8, ref showSeams))
        {
            config.ShowUvSeams = showSeams;
            config.Save();
        }

        ImUtf8.HoverTooltip("Outlines where the model's UV islands end.\nA decal crossing one of these lines gets cut off there on the actual gear."u8);

        if (option.Slot is TextureSlot.Index)
        {
            ImGui.SameLine();
            ImUtf8.Checkbox("Colorize"u8, ref _colorizeIdMap);
            ImUtf8.HoverTooltip(
                "Render each id texel in the colorset color it selects (with your edits applied) instead of the near-black raw map — colorset decals become visible in their set colors."u8);
        }

        var wrap = SelectWrap(dTexture, option, entry);
        if (wrap == null)
        {
            ImUtf8.Text(entry.Building ? "Compositing..."u8 : "No preview available for this texture."u8);
            return;
        }

        var layerCount = dTexture.Data.Textures.GetValueOrDefault(option.GamePath)?.Count(l => l.Enabled) ?? 0;
        ImUtf8.Text($"{option.GamePath}  —  {entry.Pristine.Width}x{entry.Pristine.Height}"
          + (layerCount > 0 ? $", {layerCount} layer(s)" : string.Empty)
          + (entry.Building ? "  (updating...)" : string.Empty));

        DrawCanvas(dTexture, option, wrap);
    }

    private IDalamudTextureWrap? DisplayWrap(CompositePreviewCache.Entry entry)
        => _showComposited ? entry.CompositedWrap ?? entry.PristineWrap : entry.PristineWrap;

    /// <summary> The wrap to display: pristine, composited, or the colorized id-map derivative. </summary>
    private IDalamudTextureWrap? SelectWrap(DTexture dTexture, TextureOption option, CompositePreviewCache.Entry entry)
        => option.Slot is TextureSlot.Index && _colorizeIdMap
            ? GetColorizedWrap(dTexture, option, entry) ?? DisplayWrap(entry)
            : DisplayWrap(entry);

    /// <summary>
    /// The id map rendered through the resolved colorset: each texel shows the diffuse color
    /// of the pair it references, its A and B rows blended by the G channel — what the pair
    /// actually renders, with the dTexture's row edits applied.
    /// </summary>
    private IDalamudTextureWrap? GetColorizedWrap(DTexture dTexture, TextureOption option, CompositePreviewCache.Entry entry)
    {
        var source = _showComposited ? entry.Composited ?? entry.Pristine?.Rgba : entry.Pristine?.Rgba;
        if (source == null || entry.Pristine == null)
            return null;

        var rows = MaterialEditApplier.ResolveRowDiffuse(option.Mtrl,
            dTexture.Data.Materials.GetValueOrDefault(option.MaterialGamePath));
        if (rows == null)
            return null;

        // Rebuild only when the buffer or the resolved row colors actually changed —
        // comparing 32 colors is nothing next to recolorizing millions of texels.
        var key = (option.GamePath, _showComposited, entry.Version);
        if (_colorizedWrap != null && _colorizedKey == key && _colorizedRows != null && rows.AsSpan().SequenceEqual(_colorizedRows))
            return _colorizedWrap;

        var width  = entry.Pristine.Width;
        var height = entry.Pristine.Height;
        var rgba   = new byte[width * height * 4];
        for (var i = 0; i < width * height; ++i)
        {
            var color = IdMapTexel.BlendedRowColor(rows, source[i * 4], source[i * 4 + 1]);
            rgba[i * 4]     = (byte)Math.Clamp((int)(color.X * 255f), 0, 255);
            rgba[i * 4 + 1] = (byte)Math.Clamp((int)(color.Y * 255f), 0, 255);
            rgba[i * 4 + 2] = (byte)Math.Clamp((int)(color.Z * 255f), 0, 255);
            rgba[i * 4 + 3] = 255;
        }

        _colorizedWrap?.Dispose();
        _colorizedWrap = textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(width, height), rgba,
            $"DTM Colorized {option.GamePath}");
        _colorizedKey  = key;
        _colorizedRows = rows;
        return _colorizedWrap;
    }

    /// <summary> Zoom/pan canvas: wheel zooms around the cursor, left-drag pans, right-click resets. </summary>
    private void DrawCanvas(DTexture dTexture, TextureOption option, IDalamudTextureWrap wrap)
    {
        var avail = ImGui.GetContentRegionAvail();
        avail = new Vector2(MathF.Max(avail.X, 64f), MathF.Max(avail.Y, 64f));

        var start = ImGui.GetCursorScreenPos();
        ImUtf8.InvisibleButton("##textureCanvas"u8, avail);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + avail, 0xFF181A1Cu);
        drawList.PushClipRect(start, start + avail, true);

        var fit     = MathF.Min(avail.X / wrap.Width, avail.Y / wrap.Height);
        var scale   = fit * _zoom;
        var size    = new Vector2(wrap.Width, wrap.Height) * scale;
        var origin  = start + (avail - size) / 2f + _pan;
        var hovered = ImGui.IsItemHovered();
        var io      = ImGui.GetIO();

        if (hovered && io.MouseWheel != 0f)
        {
            var newZoom = Math.Clamp(_zoom * (1f + io.MouseWheel * 0.15f), 0.5f, 32f);
            if (newZoom != _zoom)
            {
                // Keep the texel under the cursor fixed while zooming.
                var mouse    = ImGui.GetMousePos();
                var texel    = (mouse - origin) / scale;
                var newScale = fit * newZoom;
                var newSize  = new Vector2(wrap.Width, wrap.Height) * newScale;
                _pan  = mouse - texel * newScale - start - (avail - newSize) / 2f;
                _zoom = newZoom;
            }
        }

        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            _pan += io.MouseDelta;

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ResetView();

        // Recompute after input so the frame reflects the new zoom immediately.
        scale  = fit * _zoom;
        size   = new Vector2(wrap.Width, wrap.Height) * scale;
        origin = start + (avail - size) / 2f + _pan;

        drawList.AddImage(wrap.Handle, origin, origin + size);

        if (config.ShowUvSeams)
        {
            var layout = GetUvLayout(dTexture, option);
            if (layout != null)
            {
                // Dark underlay below each bright line keeps the seams readable on any texture.
                const uint seamOutline = 0xC0000000u;
                const uint seamColor   = 0xFF00D7FFu;
                foreach (var (a, b) in layout.Seams)
                    drawList.AddLine(origin + a * size, origin + b * size, seamOutline, 3f);
                foreach (var (a, b) in layout.Seams)
                    drawList.AddLine(origin + a * size, origin + b * size, seamColor, 1.5f);
            }
        }

        drawList.PopClipRect();

        if (hovered)
            ImUtf8.HoverTooltip("Wheel: zoom.  Left-drag: pan.  Right-click: reset view."u8);
    }

    private UvLayout? GetUvLayout(DTexture dTexture, TextureOption option)
    {
        var source = dTexture.Data.Source.Materials.FirstOrDefault(m
            => string.Equals(m.GamePath, option.MaterialGamePath, StringComparison.OrdinalIgnoreCase));
        return source == null ? null : uvReader.Get(source);
    }
}
