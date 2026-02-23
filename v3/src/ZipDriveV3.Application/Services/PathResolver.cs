using ZipDriveV3.Domain.Abstractions;
using ZipDriveV3.Domain.Models;

namespace ZipDriveV3.Application.Services;

/// <summary>
/// Normalizes raw virtual paths and resolves them against the archive trie.
/// </summary>
public sealed class PathResolver : IPathResolver
{
    private readonly IArchiveTrie _archiveTrie;

    public PathResolver(IArchiveTrie archiveTrie)
    {
        _archiveTrie = archiveTrie;
    }

    public ArchiveTrieResult Resolve(string? virtualPath)
    {
        string normalized = Normalize(virtualPath);
        return _archiveTrie.Resolve(normalized);
    }

    /// <summary>
    /// Normalizes a raw path: convert backslashes, trim slashes, collapse doubles.
    /// </summary>
    internal static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        // Replace backslashes with forward slashes
        string result = path.Replace('\\', '/');

        // Trim leading and trailing slashes
        result = result.Trim('/');

        // Collapse consecutive slashes
        while (result.Contains("//"))
            result = result.Replace("//", "/");

        return result;
    }
}
