using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Format-specific builder that parses an archive file and produces an ArchiveStructure.
/// Each archive format (ZIP, RAR, 7Z) implements this interface.
///
/// Called by ArchiveStructureCache on cache miss. The cache handles
/// caching, eviction, and thundering herd prevention — the builder just parses.
/// </summary>
public interface IArchiveStructureBuilder
{
    /// <summary>Format identifier (e.g., "zip", "rar", "7z").</summary>
    string FormatId { get; }

    /// <summary>File extensions this builder handles (e.g., [".zip"], [".rar"]).</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Builds an ArchiveStructure by parsing the archive file.
    /// Must populate the trie with ArchiveEntryInfo entries and synthesize parent directories.
    /// May also populate format-specific internal metadata stores as a side effect.
    /// </summary>
    Task<ArchiveStructure> BuildAsync(
        string archiveKey,
        string absolutePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lightweight probe to detect unsupported archive variants (e.g., solid RAR)
    /// before trie registration. Reads only the file header (~30 bytes) — does NOT
    /// instantiate any archive library. Must be fast (&lt; 0.1ms).
    /// Default implementation returns IsSupported=true (no unsupported variants).
    /// </summary>
    Task<ArchiveProbeResult> ProbeAsync(
        string absolutePath,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ArchiveProbeResult(true));
}
