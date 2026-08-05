using Luna;

namespace DynamicTextureManager.Services;

public interface ISavable : ISavable<FilenameService>
{ }

public sealed class SaveService(LunaLogger log, FrameworkManager framework, FilenameService fileNames)
    : BaseSaveService<FilenameService>(log, framework, fileNames);
