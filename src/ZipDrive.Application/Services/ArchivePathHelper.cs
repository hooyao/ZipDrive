namespace ZipDrive.Application.Services;

/// <summary>
/// Shared path normalization between FileSystemWatcher events and ArchiveDiscovery.
/// Ensures both produce identical virtual path strings.
/// </summary>
public static class ArchivePathHelper
{
    /// <summary>
    /// Converts an absolute file path to a virtual path relative to rootPath.
    /// Normalizes: GetFullPath on both inputs, forward slashes, no leading slash.
    /// Handles 8.3 short names via GetFullPath normalization.
    /// </summary>
    public static string ToVirtualPath(string rootPath, string absolutePath)
    {
        string normalizedRoot = Path.GetFullPath(rootPath);
        string normalizedFile = Path.GetFullPath(absolutePath);
        string relative = Path.GetRelativePath(normalizedRoot, normalizedFile);
        return relative.Replace('\\', '/');
    }
}
