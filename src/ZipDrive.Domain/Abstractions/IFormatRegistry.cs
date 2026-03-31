namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Central registry for archive format providers. Resolves providers by format ID
/// and detects format from file path/extension.
/// Implementation collects all registered IArchiveStructureBuilder, IArchiveEntryExtractor,
/// and IPrefetchStrategy via DI (IEnumerable&lt;T&gt;) and indexes by FormatId.
/// </summary>
public interface IFormatRegistry
{
    /// <summary>Returns the structure builder for the given format.</summary>
    /// <exception cref="NotSupportedException">No builder registered for this format.</exception>
    IArchiveStructureBuilder GetStructureBuilder(string formatId);

    /// <summary>Returns the entry extractor for the given format.</summary>
    /// <exception cref="NotSupportedException">No extractor registered for this format.</exception>
    IArchiveEntryExtractor GetExtractor(string formatId);

    /// <summary>Returns the prefetch strategy for the given format, or null if none.</summary>
    IPrefetchStrategy? GetPrefetchStrategy(string formatId);

    /// <summary>
    /// Detects the format of an archive file by extension (and optionally magic bytes).
    /// Returns null if the file is not a recognized archive format.
    /// </summary>
    string? DetectFormat(string filePath);

    /// <summary>
    /// All file extensions supported across all registered providers (e.g., [".zip", ".rar"]).
    /// Used by ArchiveDiscovery and FileSystemWatcher.
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Notifies all providers that an archive has been removed (dynamic reload / invalidation).
    /// Providers with format-specific metadata stores (e.g., ZipFormatMetadataStore) clean up here.
    /// </summary>
    void OnArchiveRemoved(string archiveKey);
}
