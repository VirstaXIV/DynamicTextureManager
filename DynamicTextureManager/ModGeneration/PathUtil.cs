using System;

namespace DynamicTextureManager.ModGeneration;

public static class PathUtil
{
    /// <summary>
    /// Whether a file path lies inside a directory, tolerant of mixed separators —
    /// Penumbra IPC and local Path operations do not agree on slashes under Wine.
    /// </summary>
    public static bool IsInside(string path, string directory)
    {
        if (path.Length == 0 || directory.Length == 0)
            return false;

        var normalizedPath = Normalize(path);
        var normalizedDir  = Normalize(directory).TrimEnd('/');
        return normalizedPath.StartsWith(normalizedDir + '/', StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/');
}
