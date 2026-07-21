using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DynamicTextureManager.DTextures.History;
using DynamicTextureManager.Events;
using DynamicTextureManager.Services;
using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.DTextures;

public sealed class DTextureManager : DTextureEditor
{
    public readonly DTextureStorage DTextures;
    
    public DTextureManager(SaveService saveService, DTextureChanged @event, DTextureStorage storage, Configuration config)
        : base(saveService, @event, config)
    {
        DTextures = storage;

        LoadDTextures();
        CreateDTexturesFolder(saveService);
    }
    
    #region DTexture Management
    
    private void LoadDTextures()
    {
        var stopwatch = Stopwatch.StartNew();
        DTextures.Clear();
        var skipped = 0;
        ThreadLocal<List<(DTexture, string)>> designs = new(() => [], true);
        Parallel.ForEach(SaveService.FileNames.DTextures(), (f, _) =>
        {
            try
            {
                var text   = File.ReadAllText(f.FullName);
                var data   = JObject.Parse(text);
                var design = DTexture.LoadDTexture(data);
                designs.Value!.Add((design, f.FullName));
            }
            catch (Exception ex)
            {
                DynamicTextureManager.Log.Error($"Could not load dTexture, skipped:\n{ex}");
                Interlocked.Increment(ref skipped);
            }
        });

        List<(DTexture, string)> invalidNames = [];
        foreach (var (dTexture, path) in designs.Values.SelectMany(v => v))
        {
            if (dTexture.Identifier.ToString() != Path.GetFileNameWithoutExtension(path))
                invalidNames.Add((dTexture, path));
            if (DTextures.Contains(dTexture.Identifier))
            {
                DynamicTextureManager.Log.Error($"Could not load dTexture, skipped: Identifier {dTexture.Identifier} was not unique.");
                ++skipped;
                continue;
            }

            dTexture.Index = DTextures.Count;
            DTextures.Add(dTexture);
        }

        var failed = MoveInvalidNames(invalidNames);
        if (invalidNames.Count > 0)
            DynamicTextureManager.Log.Information(
                $"Moved {invalidNames.Count - failed} designs to correct names.{(failed > 0 ? $" Failed to move {failed} designs to correct names." : string.Empty)}");

        DynamicTextureManager.Log.Information(
            $"Loaded {DTextures.Count} dTextures in {stopwatch.ElapsedMilliseconds} ms.{(skipped > 0 ? $" Skipped loading {skipped} designs due to errors." : string.Empty)}");
    }
    
    private static void CreateDTexturesFolder(SaveService service)
    {
        var ret = service.FileNames.DTextureDirectory;
        if (Directory.Exists(ret))
            return;

        try
        {
            Directory.CreateDirectory(ret);
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Error($"Could not create dTexture folder directory at {ret}:\n{ex}");
        }
    }
    
    private int MoveInvalidNames(IEnumerable<(DTexture, string)> invalidNames)
    {
        var failed = 0;
        foreach (var (dTexture, name) in invalidNames)
        {
            try
            {
                var correctName = SaveService.FileNames.DTextureFile(dTexture);
                File.Move(name, correctName, false);
                DynamicTextureManager.Log.Information($"Moved invalid dTexture file from {Path.GetFileName(name)} to {Path.GetFileName(correctName)}.");
            }
            catch (Exception ex)
            {
                ++failed;
                DynamicTextureManager.Log.Error($"Failed to move invalid dTexture file from {Path.GetFileName(name)}:\n{ex}");
            }
        }

        return failed;
    }
    
    /// <summary> Create a new empty dTexture of the given name, saved and added to storage. </summary>
    public DTexture CreateEmpty(string name, string? path = null)
    {
        var dTexture = new DTexture
        {
            Identifier   = Guid.NewGuid(),
            CreationDate = DateTimeOffset.UtcNow,
            Name         = name,
            Index        = DTextures.Count,
        };
        dTexture.LastEdit = dTexture.CreationDate;

        DTextures.Add(dTexture);
        SaveService.ImmediateSave(dTexture);
        DynamicTextureManager.Log.Debug($"Added new dTexture {dTexture.Identifier}.");
        DTextureChanged.Invoke(DTextureChanged.Type.Created, dTexture, new CreationTransaction(name, path));
        return dTexture;
    }

    /// <summary> Create a copy of an existing dTexture under a new name. </summary>
    public DTexture CreateClone(DTexture other, string name, string? path = null)
    {
        var dTexture = new DTexture(other)
        {
            Identifier   = Guid.NewGuid(),
            CreationDate = DateTimeOffset.UtcNow,
            Name         = name,
            Index        = DTextures.Count,
        };
        dTexture.LastEdit = dTexture.CreationDate;
        // The clone gets its own generated mod, so do not inherit build state.
        dTexture.Data.OutputModDirectory = string.Empty;
        dTexture.Data.LastBuiltHash      = string.Empty;

        DTextures.Add(dTexture);
        SaveService.ImmediateSave(dTexture);
        DynamicTextureManager.Log.Debug($"Added new dTexture {dTexture.Identifier} by cloning {other.Identifier}.");
        DTextureChanged.Invoke(DTextureChanged.Type.Created, dTexture, new CreationTransaction(name, path));
        return dTexture;
    }

    public void Delete(DTexture dTexture)
    {
        foreach (var d in DTextures.Skip(dTexture.Index + 1))
            --d.Index;
        DTextures.RemoveAt(dTexture.Index);
        SaveService.ImmediateDelete(dTexture);
        DTextureChanged.Invoke(DTextureChanged.Type.Deleted, dTexture, null);
    }
    
    #endregion

    #region Edit Information
    
    /// <summary> Rename a dTexture. </summary>
    public void Rename(DTexture dTexture, string newName)
    {
        var oldName = dTexture.Name.Text;
        if (oldName == newName)
            return;

        dTexture.Name     = newName;
        dTexture.LastEdit = DateTimeOffset.UtcNow;
        SaveService.QueueSave(dTexture);
        DynamicTextureManager.Log.Debug($"Renamed dTexture {dTexture.Identifier}.");
        DTextureChanged.Invoke(DTextureChanged.Type.Renamed, dTexture, new RenameTransaction(oldName, newName));
    }
    
    #endregion
}