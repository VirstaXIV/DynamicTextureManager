using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures;
using DynamicTextureManager.Events;
using DynamicTextureManager.Interop;
using DynamicTextureManager.Services;
using IService = Luna.IService;
using Penumbra.Api.Enums;
using Penumbra.GameData.Files.MaterialStructs;

namespace DynamicTextureManager.ModGeneration;

// The build pipeline: Apply gathers sources on the framework thread into a BuildPlan,
// BuildAndWriteAsync composites and BC-compresses in the background, and the result is
// registered or reloaded in Penumbra.
public sealed partial class OverlayModManager
{
    private sealed record TextureJob(string GamePath, string? DiskPath, List<DTextures.Data.TextureLayer> Layers, MaterialMesh? Mesh)
    {
        /// <summary> Sibling-texture slot this job applies material effects for (normal/mask), if any. </summary>
        public Shaders.TextureSlot EffectSlot { get; init; } = Shaders.TextureSlot.Unknown;

        /// <summary> Decal layers from the material's other textures whose effects replay onto this one. </summary>
        public List<DTextures.Data.TextureLayer> EffectLayers { get; init; } = [];
    }

    private sealed record BuildPlan(Dictionary<string, byte[]> MaterialFiles, List<TextureJob> TextureJobs,
        List<AnimatedHairJob> AnimatedJobs);

    /// <summary>
    /// Build the overlay mod for a dTexture and register or reload it in Penumbra.
    /// Source gathering happens on the calling (framework) thread; texture compositing and
    /// BC compression run in the background, then registration hops back to the framework.
    /// </summary>
    public bool Apply(DTexture dTexture)
    {
        if (Busy)
            return Fail("A build is already running.");
        if (!penumbra.Available)
            return Fail("Penumbra is not available.");

        Busy = true;
        string    dirName, modDirectory;
        bool      isNew, cleaning;
        BuildPlan plan;
        try
        {
            var modRoot = penumbra.GetModDirectory();
            if (modRoot.Length == 0 || !Directory.Exists(modRoot))
            {
                Busy = false;
                return Fail($"Penumbra mod directory \"{modRoot}\" does not exist.");
            }

            dirName      = ModDirectoryName(dTexture);
            modDirectory = Path.Combine(modRoot, dirName);
            isNew        = !Directory.Exists(modDirectory);

            // Removing the last decal or source material must clean the built mod too — its
            // old baked files keep applying otherwise. With nothing left to build, an EXISTING
            // mod gets an empty commit (the per-file commit deletes everything stale); without
            // a built mod there is nothing to clean and the request fails like before.
            var emptyReason = dTexture.Data.Source.IsEmpty ? "No source selected."
                : !dTexture.Data.HasEdits ? "No edits to apply."
                : null;
            plan = emptyReason == null
                ? PrepareBuild(dTexture, modDirectory)
                : new BuildPlan(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase), [], []);
            cleaning = plan.MaterialFiles.Count == 0 && plan.TextureJobs.Count == 0;
            if (cleaning && isNew)
            {
                Busy = false;
                return Fail(emptyReason ?? "No files could be built from the current edits.");
            }
        }
        catch (Exception ex)
        {
            Busy = false;
            DynamicTextureManager.Log.Error($"Failed to prepare build for dTexture {dTexture.Identifier}:\n{ex}");
            return Fail($"Build failed: {ex.Message}");
        }

        // Live customize state must be read here on the framework thread, never from the
        // background build — the baked file freezes the colors captured now.
        var characterColors = new CharacterColors();
        if (plan.TextureJobs.Any(j => j.Layers.Concat(j.EffectLayers)
                .Any(l => l is DTextures.Data.ProceduralSurfaceLayer { Enabled: true, UseCharacterColors: true })))
        {
            if (hairColors.TryGetLocalPlayerHair(out var liveHair))
                characterColors = characterColors with
                {
                    HairMain = liveHair.Main,
                    // Highlights disabled leaves no second color — lighten the main a touch
                    // so fur crests still separate from the base.
                    HairHighlight = liveHair.HighlightsEnabled
                        ? liveHair.Highlight
                        : System.Numerics.Vector3.Min(liveHair.Main * 1.35f + new System.Numerics.Vector3(0.06f), System.Numerics.Vector3.One),
                };
        }

        LastResult = plan.TextureJobs.Count > 0 ? "Building textures..." : "Building...";
        _ = Task.Run(async () =>
        {
            try
            {
                var written = await BuildAndWriteAsync(dTexture, modDirectory, plan, characterColors, commitWhenEmpty: cleaning).ConfigureAwait(false);
                await framework.RunOnFrameworkThread(() =>
                {
                    if (written == 0 && !cleaning)
                    {
                        Fail("No files could be built from the current edits.");
                        return;
                    }

                    var (ec, statusDetail) = RegisterOrReload(dTexture, dirName, isNew);
                    if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
                    {
                        Fail($"Penumbra rejected the mod: {ec}.");
                        return;
                    }

                    dTexture.Data.OutputModDirectory = dirName;
                    saveService.QueueSave(dTexture);
                    InvalidateCaches();

                    // Redraw EVERYONE, not just the player: body skin textures are shared —
                    // any other actor using the same file (retainers, synced players) keeps
                    // the old texture referenced, and a cached resource never reloads while
                    // referenced. Redrawing only the player left every rebuild invisible.
                    penumbra.RedrawAll();
                    LastResult = cleaning
                        ? $"Cleared mod \"{dirName}\" — nothing left to apply, its old files were removed."
                        : $"Applied {written} file(s) as mod \"{dirName}\"{statusDetail}.";
                    DynamicTextureManager.Log.Information(LastResult);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Error($"Failed to apply dTexture {dTexture.Identifier}:\n{ex}");
                LastResult = $"Build failed: {ex.Message}";
            }
            finally
            {
                Busy = false;
            }
        });
        return true;
    }

    /// <summary> Gather all source inputs on the calling thread so the background build needs no further IPC. </summary>
    private BuildPlan PrepareBuild(DTexture dTexture, string modDirectory)
    {
        var materials = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, edit) in dTexture.Data.Materials.Where(kvp => !kvp.Value.IsEmpty))
        {
            var source = dTexture.Data.Source.Materials.FirstOrDefault(m
                => string.Equals(m.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                DynamicTextureManager.Log.Warning($"Material {gamePath} has edits but is not part of the source, skipped.");
                continue;
            }

            var mtrl = sourceFiles.GetMaterial(source, modDirectory);
            if (mtrl == null)
                continue;

            if (mtrl.Table is not ColorTable)
            {
                // Skin/legacy materials never carry row edits (their decals are texture-only);
                // only warn when actual colorset edits would be dropped.
                if (edit.Rows.Count > 0)
                    DynamicTextureManager.Log.Warning(
                        $"Material {gamePath} has colorset row edits but no Dawntrail color table (shader {mtrl.ShaderPackage.Name}) — colorset edits require one, skipped.");
                continue;
            }

            if (MaterialEditApplier.Apply(mtrl, edit) == 0)
                continue;

            materials[gamePath] = mtrl.Write();
        }

        // Layer stacks can outlive their material: removing a source while another material
        // fails to load skips pruning (deliberately — a transient failure must never delete
        // layers), leaving stacks nothing in the UI shows anymore. Those must never build,
        // or ghost decals from removed sources keep shipping invisibly. When any source
        // material cannot be enumerated the filter is skipped — incomplete data must not
        // drop legitimate stacks either.
        HashSet<string>? exposed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var source in dTexture.Data.Source.Materials)
        {
            var sourceMtrl = sourceFiles.GetMaterial(source, modDirectory);
            if (sourceMtrl == null)
            {
                exposed = null;
                break;
            }

            foreach (var info in shaderHandlers.For(sourceMtrl).ClassifyTextures(sourceMtrl))
                exposed?.Add(info.GamePath);
        }

        var textures = new List<TextureJob>();
        // A texture needs a job when any layer stamps onto it — or when an extraction
        // redirected its source to a cleaned copy: that base must ship even with every
        // layer disabled, otherwise the source mod's file (baked decal included) resolves
        // again and "disabled" would un-hide the extracted decal.
        foreach (var (gamePath, layers) in dTexture.Data.Textures.Where(kvp
                     => kvp.Value.Any(l => l.Enabled || l is DTextures.Data.DecalLayer { Extracted: true, PreExtractionSource: not null })))
        {
            if (exposed != null && !exposed.Contains(gamePath))
            {
                DynamicTextureManager.Log.Warning(
                    $"Texture {gamePath} has {layers.Count} layer(s) but no current source material exposes it — skipped (leftover from a removed source).");
                continue;
            }

            // Always bake from the pristine source captured when the layer was added — a
            // build-time resolve would return our own generated file and compound the bake.
            var diskPath = GetOrCaptureTextureSource(dTexture, gamePath);

            // Surface-projected layers bake through the material's bind-pose mesh.
            MaterialMesh? mesh = null;
            if (layers.Any(l => l.Enabled && l.NeedsMeshGeometry))
            {
                var owner = CompositePlanner.FindTextureOwner(dTexture.Data, gamePath, shaderHandlers, sourceFiles);
                mesh = owner != null ? uvReader.GetMesh(owner) : null;
                if (mesh == null)
                    DynamicTextureManager.Log.Warning(
                        $"No mesh geometry for {gamePath} — surface decals and UV-aware hair zones fall back this build.");
            }

            textures.Add(new TextureJob(gamePath, diskPath is { Length: > 0 } ? diskPath : null, layers, mesh));
        }

        AddSiblingEffectJobs(dTexture, textures);
        AddOverlayCompanionJobs(dTexture, textures);

        var animated = PrepareAnimatedHair(dTexture, modDirectory, materials, textures);

        // One compact line of what actually builds — the first thing to check when a decal
        // ships that the UI no longer shows.
        DynamicTextureManager.Log.Debug(
            $"Build plan: {materials.Count} material(s); {animated.Count} animated hair conversion(s); "
          + $"textures [{string.Join(", ", textures.Select(t => $"{t.GamePath} ({t.Layers.Count} layer(s))"))}]");

        return new BuildPlan(materials, textures, animated);
    }

    /// <summary>
    /// All textures of a material are related: decals with material effects (normal
    /// smoothing, mask finish) replay their footprint onto the material's normal/mask
    /// textures, which usually have no layers of their own — synthesize jobs for them.
    /// The discovery itself is shared with the preview cache via <see cref="CompositePlanner"/>.
    /// </summary>
    private void AddSiblingEffectJobs(DTexture dTexture, List<TextureJob> textures)
    {
        var meshCache = new Dictionary<string, MaterialMesh?>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in CompositePlanner.SiblingEffectTargets(dTexture.Data, shaderHandlers, sourceFiles))
        {
            MaterialMesh? mesh = null;
            if (target.NeedsMesh)
            {
                if (!meshCache.TryGetValue(target.Owner.GamePath, out mesh))
                    meshCache[target.Owner.GamePath] = mesh = uvReader.GetMesh(target.Owner);
                if (mesh == null)
                    DynamicTextureManager.Log.Warning(
                        $"No mesh geometry for {target.Owner.GamePath} — surface decal material effects will be skipped this build.");
            }

            var existing = textures.FindIndex(j => string.Equals(j.GamePath, target.GamePath, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                var job = textures[existing];
                textures[existing] = job with
                {
                    EffectSlot = target.Slot,
                    EffectLayers = [.. job.EffectLayers, .. target.Layers],
                    Mesh = job.Mesh ?? mesh,
                };
                continue;
            }

            // Capture the sibling's pristine source BEFORE our own mod first claims its
            // resolution — later resolves would return our generated file and compound.
            var diskPath = GetOrCaptureTextureSource(dTexture, target.GamePath);
            textures.Add(new TextureJob(target.GamePath, diskPath is { Length: > 0 } ? diskPath : null, [], mesh)
            {
                EffectSlot   = target.Slot,
                EffectLayers = target.Layers,
            });
        }
    }

    /// <summary>
    /// Overlay-part textures (nails, accents — added as their own source materials) an enabled
    /// body-skin surface decal's footprint overlaps: the SAME layer reprojects onto the
    /// overlay's own mesh through the normal decal-application path (not a material-effect
    /// replay — this makes the tattoo itself appear there, in full color), so it continues
    /// seamlessly across the seam. One source of truth — no separate decal layers to keep in
    /// sync when the user edits or moves the original. Discovery shared with the preview cache
    /// via <see cref="CompositePlanner"/>, so the viewport shows the same result.
    /// </summary>
    private void AddOverlayCompanionJobs(DTexture dTexture, List<TextureJob> textures)
    {
        foreach (var target in CompositePlanner.OverlayCompanionTargets(dTexture.Data, shaderHandlers, sourceFiles, uvReader))
        {
            var mesh = uvReader.GetMesh(target.Owner);
            if (mesh == null)
            {
                DynamicTextureManager.Log.Warning(
                    $"No mesh geometry for {target.Owner.GamePath} — a body tattoo overlapping it will be skipped this build.");
                continue;
            }

            var existing = textures.FindIndex(j => string.Equals(j.GamePath, target.GamePath, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                var job = textures[existing];
                textures[existing] = job with { Layers = [.. job.Layers, .. target.Layers], Mesh = job.Mesh ?? mesh };
                continue;
            }

            var diskPath = GetOrCaptureTextureSource(dTexture, target.GamePath);
            textures.Add(new TextureJob(target.GamePath, diskPath is { Length: > 0 } ? diskPath : null, [.. target.Layers], mesh));
        }
    }

    /// <summary>
    /// The pristine source file of a layered texture: the stored capture, else a fresh
    /// resolve (rejecting our own generated mod), else a search through the source mods'
    /// own file lists — the recovery path when our mod already owns the resolution.
    /// Empty string means vanilla, null means unknown. Successful captures are persisted.
    /// </summary>
    public string? GetOrCaptureTextureSource(DTexture dTexture, string gamePath)
    {
        if (dTexture.Data.TextureSourcePaths.TryGetValue(gamePath, out var stored))
        {
            // A capture pointing into ANY generated overlay is poisoned — a remove/re-add
            // race can capture while an overlay still owns the resolution. Recapture instead
            // of baking generated output back in as "pristine". A capture whose file is gone
            // (source mod updated or removed) must recapture too — decoding would silently
            // fall back to vanilla and downgrade a hi-res base.
            if (!IsGeneratedModFile(stored) && (stored.Length == 0 || File.Exists(stored)))
                return stored;

            DynamicTextureManager.Log.Warning(
                $"Texture source of {gamePath} {(IsGeneratedModFile(stored) ? "pointed into a generated mod" : "no longer exists")} (\"{stored}\") — dropping it and recapturing.");
            dTexture.Data.TextureSourcePaths.Remove(gamePath);
        }

        if (!penumbra.Available)
            return null;

        string? found = null;
        try
        {
            var modRoot = penumbra.GetModDirectory();

            var resolved = penumbra.ResolvePlayerPath(gamePath);
            if (string.Equals(resolved, gamePath, StringComparison.OrdinalIgnoreCase))
                found = string.Empty; // vanilla
            else if (!IsGeneratedModFile(resolved))
                found = resolved;
            else
                foreach (var sourceMod in dTexture.Data.Source.Materials
                             .Select(m => m.ModDirectory)
                             .Where(m => m.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    found = ModFileLocator.Find(Path.Combine(modRoot, sourceMod), gamePath);
                    if (found != null)
                    {
                        DynamicTextureManager.Log.Information(
                            $"Recovered pristine source of {gamePath} from source mod {sourceMod}.");
                        break;
                    }
                }

            DynamicTextureManager.Log.Debug(
                $"Captured texture source of {gamePath}: {(found == null ? "(none)" : found.Length == 0 ? "(vanilla)" : $"\"{found}\"")}.");
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not capture source of texture {gamePath}: {ex.Message}");
            return null;
        }

        if (found == null)
        {
            DynamicTextureManager.Log.Warning(
                $"Texture {gamePath} has no recoverable source — our own mod owns its resolution and no source mod provides it. Falling back to vanilla.");
            return null;
        }

        dTexture.Data.TextureSourcePaths[gamePath] = found;
        saveService.QueueSave(dTexture);
        return found;
    }

    /// <summary>
    /// Whether a disk path points into any of our own generated overlay mods — never a
    /// pristine source. Checked two ways: by directory identity against every dTexture's
    /// currently tracked <see cref="DTextures.DTextureData.OutputModDirectory"/> (renames are
    /// tracked via <see cref="OnPenumbraModMoved"/>, so this catches a mod the user or Penumbra
    /// renamed away from the "DTM_" prefix — a real poisoning vector: a rename made the name
    /// check below blind to the mod, so its own baked output got captured and persisted as
    /// "pristine" forever, surviving even a removed/re-added decal), and by the "DTM_" name
    /// prefix as a fallback for mods not yet tracked (e.g. mid-build, or another dTexture this
    /// session hasn't loaded from storage).
    /// </summary>
    private bool IsGeneratedModFile(string path)
    {
        if (path.Length == 0 || !Path.IsPathRooted(path))
            return false;

        try
        {
            var modRoot = penumbra.GetModDirectory();
            if (modRoot.Length == 0 || !PathUtil.IsInside(path, modRoot))
                return false;

            foreach (var dTexture in storage)
            {
                var dir = dTexture.Data.OutputModDirectory;
                if (dir.Length > 0 && PathUtil.IsInside(path, Path.Combine(modRoot, dir)))
                    return true;
            }

            var firstSegment = Path.GetRelativePath(modRoot, path).Split('/', '\\')[0];
            return firstSegment.StartsWith("DTM_", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Background part of the build: decode, composite and BC-compress textures, then commit
    /// the folder. <paramref name="commitWhenEmpty"/> commits a deliberately empty build (a
    /// cleanup that deletes the mod's stale files); an ACCIDENTALLY empty result — every job
    /// failed to decode — must never commit, or it would wipe a previously good mod.
    /// </summary>
    private async Task<int> BuildAndWriteAsync(DTexture dTexture, string modDirectory, BuildPlan plan,
        CharacterColors characterColors, bool commitWhenEmpty = false)
    {
        using var build   = modWriter.StartBuild(modDirectory);
        var       written = 0;

        foreach (var (gamePath, bytes) in plan.MaterialFiles)
        {
            build.WriteFile(gamePath, bytes);
            ++written;
        }

        // Composited normals + masks the animated-hair conversions derive their companions from.
        var animatedInputs = new Dictionary<string, (byte[] Rgba, int Width)>(StringComparer.OrdinalIgnoreCase);

        foreach (var job in plan.TextureJobs)
        {
            var decoded = textureIO.Load(job.GamePath, job.DiskPath, modDirectory);
            if (decoded == null)
                continue;

            DynamicTextureManager.Log.Debug($"Building {job.GamePath} at {decoded.Width}x{decoded.Height} (source {(job.DiskPath == null ? "vanilla" : $"\"{job.DiskPath}\"")}).");
            var rgba = compositor.CompositeFull(decoded, job.Layers, job.EffectLayers, job.EffectSlot, job.Mesh, characterColors);

            if (plan.AnimatedJobs.Any(a => string.Equals(a.NormalGamePath, job.GamePath, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(a.MaskGamePath, job.GamePath, StringComparison.OrdinalIgnoreCase)))
                animatedInputs[job.GamePath] = (rgba, decoded.Width);

            var outFile = build.PrepareFile(job.GamePath);
            await penumbra.ConvertTextureData(rgba, decoded.Width, outFile, TextureType.Bc7Tex).ConfigureAwait(false);
            ++written;
        }

        foreach (var job in plan.AnimatedJobs)
        {
            if (!animatedInputs.TryGetValue(job.NormalGamePath, out var normal))
            {
                DynamicTextureManager.Log.Warning(
                    $"Animated hair companions for {job.MaterialGamePath} skipped — its normal {job.NormalGamePath} did not build.");
                continue;
            }

            // The id map carries exact colorset routing bytes and the reference keeps these
            // uncompressed — only the strand-detail normal and mask take BC7.
            await penumbra.ConvertTextureData(AnimatedHairBuilder.BuildNormalRgba(normal.Rgba), normal.Width,
                build.PrepareFile(job.Paths.Normal), TextureType.Bc7Tex).ConfigureAwait(false);
            await penumbra.ConvertTextureData(AnimatedHairBuilder.BuildIdRgba(normal.Rgba, job.FullCoverage), normal.Width,
                build.PrepareFile(job.Paths.Id), TextureType.RgbaTex).ConfigureAwait(false);

            // Real per-strand shading: mask derived from the composited hair mask; the flat
            // white reference tile only when the material has no mask or it failed to build.
            if (job.MaskGamePath.Length > 0 && animatedInputs.TryGetValue(job.MaskGamePath, out var mask))
                await penumbra.ConvertTextureData(AnimatedHairBuilder.BuildCharMaskRgba(mask.Rgba), mask.Width,
                    build.PrepareFile(job.Paths.Mask), TextureType.Bc7Tex).ConfigureAwait(false);
            else
                await penumbra.ConvertTextureData(AnimatedHairBuilder.BuildMaskRgba(), AnimatedHairBuilder.MaskSize,
                    build.PrepareFile(job.Paths.Mask), TextureType.RgbaTex).ConfigureAwait(false);

            var (effect, effectWidth) = LoadEffectImage(job.Edit);
            await penumbra.ConvertTextureData(effect, effectWidth,
                build.PrepareFile(job.Paths.Effect), TextureType.RgbaTex).ConfigureAwait(false);

            written += 4;
            DynamicTextureManager.Log.Debug($"Animated hair companions written for {job.MaterialGamePath}.");
        }

        if (written > 0 || commitWhenEmpty)
            build.Commit(ModName(dTexture), DynamicTextureManager.Version);

        return written;
    }

    private (PenumbraApiEc Ec, string StatusDetail) RegisterOrReload(DTexture dTexture, string dirName, bool isNew)
    {
        PenumbraApiEc ec;
        if (isNew || dTexture.Data.OutputModDirectory.Length == 0)
        {
            ec = penumbra.AddMod(dirName);
            if (ec is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)
                penumbra.SetModPath(dirName, $"DynamicTextureManager/{ModName(dTexture)}");
        }
        else
        {
            ec = penumbra.ReloadMod(dirName);
            // The user may have deleted the mod in Penumbra since the last build.
            if (ec is PenumbraApiEc.ModMissing)
            {
                ec = penumbra.AddMod(dirName);
                if (ec is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)
                    penumbra.SetModPath(dirName, $"DynamicTextureManager/{ModName(dTexture)}");
            }
        }

        if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
            return (ec, string.Empty);

        // Re-ensure enabled state and priority on every apply — a failed or reverted
        // setting would otherwise leave the mod built but invisible forever.
        var (valid, _, collection) = penumbra.GetCollectionForObject(0);
        if (!valid)
            return (PenumbraApiEc.Success, " — could not determine your collection, enable it in Penumbra manually");

        var enableEc   = penumbra.TrySetMod(collection.Id, dirName, true);
        var priorityEc = penumbra.TrySetModPriority(collection.Id, dirName, EffectivePriority(dTexture));
        if (enableEc is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
        {
            DynamicTextureManager.Log.Warning($"Could not enable mod {dirName} in collection {collection.Name}: {enableEc}.");
            return (PenumbraApiEc.Success, $" — but enabling it in collection \"{collection.Name}\" failed: {enableEc}");
        }

        if (priorityEc is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
            DynamicTextureManager.Log.Warning($"Could not set priority of mod {dirName}: {priorityEc}.");

        return (PenumbraApiEc.Success, $" — enabled in collection \"{collection.Name}\" (priority {EffectivePriority(dTexture)})");
    }
}
