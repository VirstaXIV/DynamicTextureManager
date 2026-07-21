using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.Events;
using DynamicTextureManager.ModGeneration;
using DynamicTextureManager.ModGeneration.Shaders;
using OtterGui.Services;

namespace DynamicTextureManager.Services;

/// <summary>
/// Shared cache of composited texture previews: for a dTexture's texture it holds the decoded
/// pristine source and the fully composited result (layers plus sibling material effects),
/// exactly what a build would write minus the BC7 compression. Rebuilds run debounced in the
/// background; texture wraps are created lazily on the draw thread.
/// </summary>
public sealed class CompositePreviewCache : IService, IDisposable
{
    private const long DebounceMs = 500;
    private const int  MaxEntries = 8;

    public sealed class Entry
    {
        public DecodedTexture? Pristine;
        public byte[]?         Composited;
        public bool            Building;

        /// <summary> Bumped whenever the buffers change, so consumers can invalidate derived data. </summary>
        public int Version;

        internal long StaleSinceMs;
        internal long LastUsedMs;
        internal int  WrapVersion = -1;

        public IDalamudTextureWrap? PristineWrap   { get; internal set; }
        public IDalamudTextureWrap? CompositedWrap { get; internal set; }

        internal void DisposeWraps()
        {
            PristineWrap?.Dispose();
            CompositedWrap?.Dispose();
            PristineWrap   = null;
            CompositedWrap = null;
            WrapVersion    = -1;
        }
    }

    private readonly TextureIO             _textureIO;
    private readonly TextureCompositor     _compositor;
    private readonly OverlayModManager     _overlayMods;
    private readonly ModelUvReader         _uvReader;
    private readonly SourceFileProvider    _sourceFiles;
    private readonly ShaderHandlerRegistry _shaderHandlers;
    private readonly ITextureProvider      _textureProvider;
    private readonly DTextureChanged       _dTextureChanged;

    private readonly Dictionary<(Guid, string), Entry> _entries = [];

    public CompositePreviewCache(TextureIO textureIO, TextureCompositor compositor, OverlayModManager overlayMods,
        ModelUvReader uvReader, SourceFileProvider sourceFiles, ShaderHandlerRegistry shaderHandlers,
        ITextureProvider textureProvider, DTextureChanged dTextureChanged)
    {
        _textureIO       = textureIO;
        _compositor      = compositor;
        _overlayMods     = overlayMods;
        _uvReader        = uvReader;
        _sourceFiles     = sourceFiles;
        _shaderHandlers  = shaderHandlers;
        _textureProvider = textureProvider;
        _dTextureChanged = dTextureChanged;
        _dTextureChanged.Subscribe(OnDTextureChanged, DTextureChanged.Priority.OverlayModManager);
    }

    public void Dispose()
    {
        _dTextureChanged.Unsubscribe(OnDTextureChanged);
        foreach (var entry in _entries.Values)
            entry.DisposeWraps();
        _entries.Clear();
    }

    private void OnDTextureChanged(DTextureChanged.Type type, DTexture dTexture, DTextures.History.ITransaction? _)
    {
        if (type is DTextureChanged.Type.Deleted)
            Drop(dTexture.Identifier);
        else
            Invalidate(dTexture.Identifier);
    }

    /// <summary>
    /// The cached entry for one texture of a dTexture, kicking off a debounced background
    /// composite when stale. Must be called from the draw thread — wraps are (re)created here.
    /// </summary>
    public Entry Get(DTexture dTexture, string gamePath)
    {
        var now = Environment.TickCount64;
        var key = (dTexture.Identifier, gamePath.ToLowerInvariant());
        if (!_entries.TryGetValue(key, out var entry))
        {
            EvictIfFull();
            // A brand-new entry skips the debounce — there is nothing stale to coalesce yet.
            entry = new Entry { StaleSinceMs = now - DebounceMs };
            _entries[key] = entry;
        }

        entry.LastUsedMs = now;

        if (entry is { Building: false, StaleSinceMs: > 0 } && now - entry.StaleSinceMs >= DebounceMs)
            StartBuild(dTexture, gamePath, entry);

        if (entry.WrapVersion != entry.Version)
        {
            entry.DisposeWraps();
            if (entry.Pristine is { } pristine)
            {
                entry.PristineWrap = _textureProvider.CreateFromRaw(
                    RawImageSpecification.Rgba32(pristine.Width, pristine.Height), pristine.Rgba, $"DTM Source {gamePath}");
                if (entry.Composited is { } composited)
                    entry.CompositedWrap = _textureProvider.CreateFromRaw(
                        RawImageSpecification.Rgba32(pristine.Width, pristine.Height), composited, $"DTM Composite {gamePath}");
            }

            entry.WrapVersion = entry.Version;
        }

        return entry;
    }

    /// <summary> Mark all cached textures of a dTexture stale so they rebuild on next use. </summary>
    public void Invalidate(Guid dTextureId)
    {
        var now = Environment.TickCount64;
        foreach (var (key, entry) in _entries)
            if (key.Item1 == dTextureId)
                entry.StaleSinceMs = now;
    }

    /// <summary> Mark one cached texture of a dTexture stale. </summary>
    public void Invalidate(Guid dTextureId, string gamePath)
    {
        if (_entries.TryGetValue((dTextureId, gamePath.ToLowerInvariant()), out var entry))
            entry.StaleSinceMs = Environment.TickCount64;
    }

    private void Drop(Guid dTextureId)
    {
        foreach (var key in _entries.Keys.Where(k => k.Item1 == dTextureId).ToList())
        {
            _entries[key].DisposeWraps();
            _entries.Remove(key);
        }
    }

    private void EvictIfFull()
    {
        while (_entries.Count >= MaxEntries)
        {
            var oldest = _entries.OrderBy(kvp => kvp.Value.LastUsedMs).First();
            oldest.Value.DisposeWraps();
            _entries.Remove(oldest.Key);
        }
    }

    /// <summary>
    /// Gather all inputs on the calling (framework) thread — source capture is Penumbra IPC,
    /// mesh reads are cached — then decode and composite in the background.
    /// </summary>
    private void StartBuild(DTexture dTexture, string gamePath, Entry entry)
    {
        entry.Building     = true;
        entry.StaleSinceMs = 0;

        string? diskPath;
        List<DTextures.Data.TextureLayer> layers;
        List<DTextures.Data.TextureLayer> effectLayers = [];
        var effectSlot = TextureSlot.Unknown;
        MaterialMesh? mesh = null;
        try
        {
            diskPath = _overlayMods.GetOrCaptureTextureSource(dTexture, gamePath);
            if (diskPath is { Length: 0 })
                diskPath = null; // vanilla

            layers = dTexture.Data.Textures.GetValueOrDefault(gamePath)?.ToList() ?? [];

            var target = CompositePlanner.SiblingEffectTargets(dTexture.Data, _shaderHandlers, _sourceFiles)
                .FirstOrDefault(t => string.Equals(t.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                effectSlot   = target.Slot;
                effectLayers = target.Layers.ToList();
            }

            var needsMesh = layers.Any(l => l is DTextures.Data.DecalLayer { Surface: true, Enabled: true })
             || (target?.NeedsMesh ?? false);
            if (needsMesh)
            {
                var owner = target?.Owner ?? CompositePlanner.FindTextureOwner(dTexture.Data, gamePath, _shaderHandlers, _sourceFiles);
                mesh = owner != null ? _uvReader.GetMesh(owner) : null;
            }
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not gather preview inputs for {gamePath}: {ex.Message}");
            entry.Building = false;
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var decoded = _textureIO.Load(gamePath, diskPath, null);
                if (decoded == null)
                {
                    entry.Building = false;
                    return;
                }

                var rgba = _compositor.Composite(decoded, layers, mesh);
                if (effectLayers.Count > 0)
                    rgba = _compositor.CompositeSiblingEffects(new DecodedTexture(rgba, decoded.Width, decoded.Height),
                        effectLayers, effectSlot, mesh);

                entry.Pristine   = decoded;
                entry.Composited = rgba;
                ++entry.Version;
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Error($"Could not composite preview of {gamePath}:\n{ex}");
            }
            finally
            {
                entry.Building = false;
            }
        });
    }
}
