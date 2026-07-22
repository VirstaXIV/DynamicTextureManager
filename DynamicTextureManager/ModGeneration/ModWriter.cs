using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OtterGui.Services;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Writes the on-disk layout of a Penumbra mod: meta.json, default_mod.json and the
/// override files mirroring their game paths. Builds go into a temporary sibling directory
/// that is swapped in atomically so a failed build never leaves a broken mod behind.
/// </summary>
public sealed class ModWriter : IService
{
    public const string ModTag = "DynamicTextureManager";

    public sealed class Build : IDisposable
    {
        private readonly string                     _finalDirectory;
        private readonly string                     _buildDirectory;
        private readonly Dictionary<string, string> _files = [];
        private bool                                _committed;

        internal Build(string finalDirectory)
        {
            _finalDirectory = finalDirectory;
            _buildDirectory = finalDirectory + ".build";
            if (Directory.Exists(_buildDirectory))
                Retry(() => Directory.Delete(_buildDirectory, true), $"delete stale build directory {_buildDirectory}");
            Directory.CreateDirectory(_buildDirectory);
        }

        /// <summary> The relative path a game path's override file is stored under. </summary>
        private static string RelativePath(string gamePath)
            => gamePath.Replace('/', Path.DirectorySeparatorChar);

        /// <summary> Full path a file for the given game path should be written to; registers the redirection. </summary>
        public string PrepareFile(string gamePath)
        {
            var relative = RelativePath(gamePath);
            var full     = Path.Combine(_buildDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            _files[gamePath] = relative;
            return full;
        }

        public void WriteFile(string gamePath, byte[] data)
            => File.WriteAllBytes(PrepareFile(gamePath), data);

        public void Commit(string modName, string version)
        {
            WriteMeta(modName, version);
            WriteDefaultMod();

            // Leftover from the pre-v0.5.2 rename-swap commit; clean it up opportunistically.
            var old = _finalDirectory + ".old";
            if (Directory.Exists(old))
                try
                {
                    Retry(() => Directory.Delete(old, true), $"delete stale directory {old}");
                }
                catch (Exception ex)
                {
                    DynamicTextureManager.Log.Warning($"Could not remove stale directory {old}: {ex.Message}");
                }

            if (!Directory.Exists(_finalDirectory))
            {
                Retry(() => Directory.Move(_buildDirectory, _finalDirectory), $"move {_buildDirectory} into place");
                _committed = true;
                return;
            }

            // Existing mods are updated by replacing files IN PLACE rather than the old
            // whole-directory rename swap: Mare-family sync plugins (PlayerSync,
            // LightlessSync) watch the entire Penumbra directory and react to a directory
            // rename by re-enumerating and re-hashing the renamed tree — racing our delete
            // of it, which has crashed the game inside their watcher callbacks and also
            // held handles that made our own renames fail. Per-file replaces are the events
            // those watchers (and Penumbra) handle routinely. Every operation retries with
            // backoff to wait out transient watcher/antivirus file locks.
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(_buildDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(_buildDirectory, file);
                kept.Add(relative);
                var target = Path.Combine(_finalDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Retry(() => File.Move(file, target, true), $"replace {target}");
            }

            foreach (var file in Directory.GetFiles(_finalDirectory, "*", SearchOption.AllDirectories))
                if (!kept.Contains(Path.GetRelativePath(_finalDirectory, file)))
                    Retry(() => File.Delete(file), $"delete {file}");

            DeleteEmptySubdirectories(_finalDirectory);
            try
            {
                Retry(() => Directory.Delete(_buildDirectory, true), $"delete build directory {_buildDirectory}");
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Warning($"Could not remove build directory {_buildDirectory}: {ex.Message}");
            }

            _committed = true;
        }

        /// <summary> Waits out transient IO failures — file-cache watchers and antivirus briefly hold handles on fresh files. </summary>
        private static void Retry(Action action, string what)
        {
            for (var attempt = 0;; ++attempt)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                {
                    DynamicTextureManager.Log.Debug($"Retrying ({attempt + 1}/5) after failure to {what}: {ex.Message}");
                    System.Threading.Thread.Sleep(50 << attempt);
                }
            }
        }

        private static void DeleteEmptySubdirectories(string root)
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                DeleteEmptySubdirectories(dir);
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    try
                    {
                        Retry(() => Directory.Delete(dir), $"delete empty directory {dir}");
                    }
                    catch (Exception ex)
                    {
                        DynamicTextureManager.Log.Warning($"Could not remove empty directory {dir}: {ex.Message}");
                    }
            }
        }

        public void Dispose()
        {
            if (!_committed && Directory.Exists(_buildDirectory))
                try
                {
                    Directory.Delete(_buildDirectory, true);
                }
                catch (Exception ex)
                {
                    DynamicTextureManager.Log.Warning($"Could not clean up build directory {_buildDirectory}: {ex.Message}");
                }
        }

        private void WriteMeta(string modName, string version)
        {
            var meta = new JObject
            {
                ["FileVersion"] = 3,
                ["Name"]        = modName,
                ["Author"]      = "DynamicTextureManager",
                ["Description"] =
                    "Generated overlay mod, managed by the Dynamic Texture Manager plugin. Manual changes will be overwritten on rebuild.",
                ["Version"]     = version,
                ["Website"]     = string.Empty,
                ["ModTags"]     = new JArray(ModTag),
            };
            File.WriteAllText(Path.Combine(_buildDirectory, "meta.json"), meta.ToString(Formatting.Indented));
        }

        private void WriteDefaultMod()
        {
            var files = new JObject();
            foreach (var (gamePath, relative) in _files)
                files[gamePath] = relative;

            var defaultMod = new JObject
            {
                ["Name"]          = string.Empty,
                ["Priority"]      = 0,
                ["Files"]         = files,
                ["FileSwaps"]     = new JObject(),
                ["Manipulations"] = new JArray(),
            };
            File.WriteAllText(Path.Combine(_buildDirectory, "default_mod.json"), defaultMod.ToString(Formatting.Indented));
        }
    }

    public Build StartBuild(string modDirectory)
        => new(modDirectory);
}
