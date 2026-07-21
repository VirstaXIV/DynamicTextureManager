using System;
using System.Collections.Generic;
using System.IO;
using DynamicTextureManager.Interop;
using DynamicTextureManager.Services;
using OtterGui.Services;

namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// Shows the current (unapplied) colorset edits live on the model through a temporary
/// Penumbra mod, so editing gives immediate feedback before the persistent mod is built.
/// Sits below the row highlighter in priority so hover-highlighting stays visible while previewing.
/// </summary>
public sealed class EditPreviewer : IService, IDisposable
{
    public const string Tag      = "DTM_EditPreview";
    private const int   Priority = 9998;

    private readonly PenumbraService _penumbra;
    private readonly string          _previewDirectory;

    private string _activeGamePath = string.Empty;

    public bool Active
        => _activeGamePath.Length > 0;

    public EditPreviewer(PenumbraService penumbra, FilenameService filenames)
    {
        _penumbra         = penumbra;
        _previewDirectory = Path.Combine(filenames.ConfigDirectory, "preview");
        _penumbra.Detached += OnPenumbraDetached;
    }

    /// <summary> Redirect a game path to the given built material bytes as a temporary mod. </summary>
    public void Preview(string gamePath, byte[] mtrlBytes)
    {
        if (!_penumbra.Available)
            return;

        try
        {
            Directory.CreateDirectory(_previewDirectory);
            var file = Path.Combine(_previewDirectory, "preview.mtrl");
            File.WriteAllBytes(file, mtrlBytes);

            _penumbra.AddTemporaryModAll(Tag, new Dictionary<string, string> { [gamePath] = file }, Priority);
            _penumbra.RedrawObject(0);
            _activeGamePath = gamePath;
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not preview edits for {gamePath}: {ex.Message}");
        }
    }

    public void Clear()
    {
        if (_activeGamePath.Length == 0)
            return;

        _activeGamePath = string.Empty;
        if (!_penumbra.Available)
            return;

        try
        {
            _penumbra.RemoveTemporaryModAll(Tag, Priority);
            _penumbra.RedrawObject(0);
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not clear edit preview: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Clear();
        _penumbra.Detached -= OnPenumbraDetached;
    }

    private void OnPenumbraDetached()
        => _activeGamePath = string.Empty;
}
