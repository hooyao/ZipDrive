using ZipDriveV3.Domain.Abstractions;
using ZipDriveV3.Domain.Models;

namespace ZipDriveV3.Application.Services;

/// <summary>
/// Resolves virtual drive paths to archive + internal path.
/// Example: "\archive.zip\folder\file.txt" → ("archive.zip", "folder/file.txt")
/// </summary>
public sealed class PathResolver : IPathResolver
{
    public PathResolutionResult Resolve(string virtualPath)
    {
        // Handle null, empty, or root path
        if (string.IsNullOrWhiteSpace(virtualPath) || virtualPath == "\\")
        {
            return new PathResolutionResult(null, "", PathResolutionStatus.RootDirectory);
        }

        // Split path by backslash
        string[] parts = virtualPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return new PathResolutionResult(null, "", PathResolutionStatus.RootDirectory);
        }

        // First part is the archive key
        string archiveKey = parts[0];

        if (parts.Length == 1)
        {
            // Just the archive name, no internal path
            return new PathResolutionResult(archiveKey, "", PathResolutionStatus.ArchiveRoot);
        }

        // Rest is the internal path (convert backslashes to forward slashes)
        string internalPath = string.Join('/', parts.Skip(1));

        return new PathResolutionResult(archiveKey, internalPath, PathResolutionStatus.Success);
    }
}
