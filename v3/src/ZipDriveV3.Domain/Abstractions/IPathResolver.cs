using ZipDriveV3.Domain.Models;

namespace ZipDriveV3.Domain.Abstractions;

/// <summary>
/// Resolves virtual drive paths to archive + internal path
/// </summary>
public interface IPathResolver
{
    /// <summary>
    /// Splits a DokanNet path into archive key and internal path.
    /// Example: "\archive.zip\folder\file.txt" → ("archive.zip", "folder/file.txt")
    /// </summary>
    PathResolutionResult Resolve(string virtualPath);
}
