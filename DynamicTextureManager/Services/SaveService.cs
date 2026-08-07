using Luna;

namespace DynamicTextureManager.Services;

public interface ISavable : ISavable<FilenameService>
{ }

public sealed class SaveService(LunaLogger log, FrameworkManager framework, FilenameService fileNames)
    : BaseSaveService<FilenameService>(log, framework, fileNames)
{
    // Luna serializes on a background task while the UI thread keeps editing the live
    // object graph — a save landing mid-edit would throw or persist a torn state. These
    // overloads shadow the generic base methods for the mutable savables and capture a
    // consistent snapshot on the calling thread first; the background write then only
    // touches the snapshot. Later edits queue their own saves with fresh snapshots.
    public void QueueSave(DTextures.DTexture value)
    {
        value.CaptureSnapshot();
        base.QueueSave(value);
    }

    public void DelaySave(DTextures.DTexture value)
    {
        value.CaptureSnapshot();
        base.DelaySave(value);
    }

    public void ImmediateSave(DTextures.DTexture value)
    {
        value.CaptureSnapshot();
        base.ImmediateSave(value);
    }

    public void DelaySave(Configuration value)
    {
        value.CaptureSnapshot();
        base.DelaySave(value);
    }

    public void ImmediateSave(Configuration value)
    {
        value.CaptureSnapshot();
        base.ImmediateSave(value);
    }
}
