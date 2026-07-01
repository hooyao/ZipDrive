using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Normalizes and resolves virtual paths using the archive trie.
/// </summary>
public interface IPathResolver
{
    /// <summary>
    /// Normalizes a raw virtual path and resolves it against the archive trie.
    /// Handles backslash conversion, leading/trailing slash removal, and case normalization.
    /// </summary>
    /// <param name="virtualPath">Raw path (may use backslashes, have leading slash, etc.).</param>
    /// <returns>Resolution result with archive descriptor and internal path.</returns>
    ArchiveTrieResult Resolve(string? virtualPath);
}
