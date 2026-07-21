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
                Directory.Delete(_buildDirectory, true);
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

            if (Directory.Exists(_finalDirectory))
            {
                var old = _finalDirectory + ".old";
                if (Directory.Exists(old))
                    Directory.Delete(old, true);
                Directory.Move(_finalDirectory, old);
                Directory.Move(_buildDirectory, _finalDirectory);
                Directory.Delete(old, true);
            }
            else
            {
                Directory.Move(_buildDirectory, _finalDirectory);
            }

            _committed = true;
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
