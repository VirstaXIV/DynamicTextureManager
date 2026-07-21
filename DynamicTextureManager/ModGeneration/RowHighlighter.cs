using System;
using System.Collections.Generic;
using System.IO;
using DynamicTextureManager.Interop;
using DynamicTextureManager.Services;
using OtterGui.Services;
using Penumbra.GameData.Files;
using Penumbra.GameData.Files.MaterialStructs;
using Penumbra.GameData.Structs;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Previews on the live model which part of it a colorset row drives: overrides the material
/// through a temporary Penumbra mod with the row recolored to a loud magenta and redraws.
/// Only ever one highlight active; cleared on unhover, dispose, or Penumbra detach.
/// </summary>
public sealed class RowHighlighter : IService, IDisposable
{
    public const string Tag      = "DTM_RowHighlight";
    private const int   Priority = 9999;

    private static readonly HalfColor HighlightColor = new((Half)1f, (Half)0f, (Half)1f);

    private readonly PenumbraService _penumbra;
    private readonly string          _highlightDirectory;

    private string _activeKey = string.Empty;

    public RowHighlighter(PenumbraService penumbra, FilenameService filenames)
    {
        _penumbra           = penumbra;
        _highlightDirectory = Path.Combine(filenames.ConfigDirectory, "highlight");
        _penumbra.Detached += OnPenumbraDetached;
    }

    public bool IsActive(string gamePath, int rowIndex)
        => _activeKey == Key(gamePath, rowIndex);

    /// <summary> Highlight one colorset row of a source material on the model. </summary>
    public void Highlight(string gamePath, MtrlFile sourceMaterial, int rowIndex)
    {
        var key = Key(gamePath, rowIndex);
        if (_activeKey == key || !_penumbra.Available)
            return;

        try
        {
            if (sourceMaterial.Table is not ColorTable sourceTable || rowIndex < 0 || rowIndex >= ColorTable.NumRows)
                return;

            var mtrl = sourceMaterial.Clone();
            // Clone() copies the table by reference — replace it so the cached source stays untouched.
            var table = new ColorTable(sourceTable);
            mtrl.Table = table;

            ref var row = ref table[rowIndex];
            row.DiffuseColor  = HighlightColor;
            row.EmissiveColor = HighlightColor;

            Directory.CreateDirectory(_highlightDirectory);
            var file = Path.Combine(_highlightDirectory, "highlight.mtrl");
            File.WriteAllBytes(file, mtrl.Write());

            _penumbra.AddTemporaryModAll(Tag, new Dictionary<string, string> { [gamePath] = file }, Priority);
            _penumbra.RedrawObject(0);
            _activeKey = key;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not highlight row {rowIndex} of {gamePath}: {ex.Message}");
        }
    }

    /// <summary> Remove any active highlight. </summary>
    public void Clear()
    {
        if (_activeKey.Length == 0)
            return;

        _activeKey = string.Empty;
        if (!_penumbra.Available)
            return;

        try
        {
            _penumbra.RemoveTemporaryModAll(Tag, Priority);
            _penumbra.RedrawObject(0);
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not clear row highlight: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Clear();
        _penumbra.Detached -= OnPenumbraDetached;
    }

    private void OnPenumbraDetached()
        => _activeKey = string.Empty;

    private static string Key(string gamePath, int rowIndex)
        => $"{gamePath}#{rowIndex}";
}
