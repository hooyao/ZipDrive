using ZipDrive.Domain.Abstractions;

namespace ZipDrive.Application.Services;

/// <summary>
/// Central registry for archive format providers. Collects all registered
/// IArchiveStructureBuilder, IArchiveEntryExtractor, and IPrefetchStrategy
/// via DI (IEnumerable&lt;T&gt;) and indexes by FormatId.
/// </summary>
public sealed class FormatRegistry : IFormatRegistry
{
    private readonly Dictionary<string, IArchiveStructureBuilder> _builders;
    private readonly Dictionary<string, IArchiveEntryExtractor> _extractors;
    private readonly Dictionary<string, IPrefetchStrategy> _prefetchers;
    private readonly Dictionary<string, string> _extensionToFormat;
    private readonly List<string> _extensions;
    private readonly List<IArchiveMetadataCleanup> _cleanups;

    public FormatRegistry(
        IEnumerable<IArchiveStructureBuilder> builders,
        IEnumerable<IArchiveEntryExtractor> extractors,
        IEnumerable<IPrefetchStrategy> prefetchers)
    {
        _builders = builders.ToDictionary(b => b.FormatId, StringComparer.OrdinalIgnoreCase);
        _extractors = extractors.ToDictionary(e => e.FormatId, StringComparer.OrdinalIgnoreCase);
        _prefetchers = prefetchers.ToDictionary(p => p.FormatId, StringComparer.OrdinalIgnoreCase);

        _extensionToFormat = new(StringComparer.OrdinalIgnoreCase);
        foreach (var builder in builders)
            foreach (var ext in builder.SupportedExtensions)
                _extensionToFormat[ext] = builder.FormatId;

        _extensions = [.. _extensionToFormat.Keys];

        // Collect cleanup handlers from builders and extractors
        _cleanups = [.. builders.OfType<IArchiveMetadataCleanup>(),
                     .. extractors.OfType<IArchiveMetadataCleanup>()];
    }

    public IArchiveStructureBuilder GetStructureBuilder(string formatId) =>
        _builders.TryGetValue(formatId, out var b) ? b
        : throw new NotSupportedException($"No structure builder registered for format: {formatId}");

    public IArchiveEntryExtractor GetExtractor(string formatId) =>
        _extractors.TryGetValue(formatId, out var e) ? e
        : throw new NotSupportedException($"No entry extractor registered for format: {formatId}");

    public IPrefetchStrategy? GetPrefetchStrategy(string formatId) =>
        _prefetchers.TryGetValue(formatId, out var p) ? p : null;

    public string? DetectFormat(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        return _extensionToFormat.TryGetValue(ext, out var fmt) ? fmt : null;
    }

    public IReadOnlyList<string> SupportedExtensions => _extensions;

    public void OnArchiveRemoved(string archiveKey)
    {
        foreach (var cleanup in _cleanups)
            cleanup.CleanupArchive(archiveKey);
    }
}
