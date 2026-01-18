using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ZipDriveV3.Domain.Abstractions;
using ZipDriveV3.Domain.Models;
using ZipDriveV3.Infrastructure.Archives.Zip;
using ZipDriveV3.Infrastructure.Archives.Zip.Formats;

namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// Caches parsed ZIP archive structures using GenericCache with ObjectStorageStrategy.
/// Uses <see cref="IZipReader.StreamCentralDirectoryAsync"/> for memory-efficient parsing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Key Features:</strong>
/// <list type="bullet">
/// <item>Streaming Central Directory parsing (no bulk allocation)</item>
/// <item>Thundering herd prevention via GenericCache's Lazy&lt;Task&gt;</item>
/// <item>LRU eviction with TTL-based expiration</item>
/// <item>Memory estimation for capacity tracking</item>
/// </list>
/// </para>
/// <para>
/// <strong>Memory Estimation:</strong>
/// ~114 bytes per ZIP entry (struct + filename string + dictionary overhead).
/// </para>
/// </remarks>
public sealed class ArchiveStructureCache : IArchiveStructureCache
{
    /// <summary>
    /// Estimated memory overhead per ZIP entry in bytes.
    /// Includes: ZipEntryInfo struct (~40) + filename string (~50 avg) + dictionary overhead (~24).
    /// </summary>
    private const long BytesPerEntry = 114;

    /// <summary>
    /// Base overhead per ArchiveStructure object in bytes.
    /// Includes: ArchiveStructure object + AbsolutePath string + ArchiveKey string + DirectoryNode tree overhead.
    /// </summary>
    private const long BaseStructureOverhead = 1024;

    private readonly ICache<ArchiveStructure> _cache;
    private readonly Func<string, IZipReader> _zipReaderFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _defaultTtl;
    private readonly ILogger<ArchiveStructureCache>? _logger;

    private long _missCount;

    /// <summary>
    /// Creates a new ArchiveStructureCache.
    /// </summary>
    /// <param name="cache">Generic cache for storing ArchiveStructure objects.</param>
    /// <param name="zipReaderFactory">Factory that creates IZipReader from file path.</param>
    /// <param name="timeProvider">Optional time provider for testing.</param>
    /// <param name="defaultTtl">Default TTL for cached structures (default: 30 minutes).</param>
    /// <param name="logger">Optional logger.</param>
    public ArchiveStructureCache(
        ICache<ArchiveStructure> cache,
        Func<string, IZipReader> zipReaderFactory,
        TimeProvider? timeProvider = null,
        TimeSpan? defaultTtl = null,
        ILogger<ArchiveStructureCache>? logger = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _zipReaderFactory = zipReaderFactory ?? throw new ArgumentNullException(nameof(zipReaderFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(30);
        _logger = logger;

        _logger?.LogInformation(
            "ArchiveStructureCache initialized with TTL={TtlMinutes} minutes",
            _defaultTtl.TotalMinutes);
    }

    /// <inheritdoc />
    public async Task<ArchiveStructure> GetOrBuildAsync(
        string archiveKey,
        string absolutePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        _logger?.LogDebug("GetOrBuildAsync: {ArchiveKey} at {Path}", archiveKey, absolutePath);

        // Use GenericCache's borrow pattern - it handles thundering herd prevention
        ICacheHandle<ArchiveStructure> handle = await _cache.BorrowAsync(
            archiveKey,
            _defaultTtl,
            ct => BuildStructureAsync(archiveKey, absolutePath, ct),
            cancellationToken).ConfigureAwait(false);

        // Track our own hit/miss counts (cache already tracks hits/misses internally)
        // We can infer from cache's hit rate, but let's track explicitly for interface compliance

        ArchiveStructure structure = handle.Value;

        _logger?.LogDebug(
            "Returning structure for {ArchiveKey}: {EntryCount} entries",
            archiveKey,
            structure.EntryCount);

        return structure;
    }

    /// <summary>
    /// Builds the archive structure by streaming the Central Directory.
    /// </summary>
    private async Task<CacheFactoryResult<ArchiveStructure>> BuildStructureAsync(
        string archiveKey,
        string absolutePath,
        CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Building structure for {ArchiveKey} at {Path}", archiveKey, absolutePath);
        Interlocked.Increment(ref _missCount);

        Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        IZipReader reader = _zipReaderFactory(absolutePath);

        // Phase 1: Read EOCD
        ZipEocd eocd = await reader.ReadEocdAsync(cancellationToken).ConfigureAwait(false);

        _logger?.LogDebug(
            "EOCD: {EntryCount} entries, CD at {Offset}, size {Size}, ZIP64={IsZip64}",
            eocd.EntryCount, eocd.CentralDirectoryOffset, eocd.CentralDirectorySize, eocd.IsZip64);

        // Phase 2: Pre-allocate collections with reasonable initial capacity
        int initialCapacity = (int)Math.Min(eocd.EntryCount, 100_000);
        Dictionary<string, ZipEntryInfo> entries = new Dictionary<string, ZipEntryInfo>(initialCapacity, StringComparer.OrdinalIgnoreCase);
        DirectoryNode rootDirectory = new DirectoryNode { Name = "", FullPath = "" };

        long totalUncompressedSize = 0;
        long totalCompressedSize = 0;
        int entryCount = 0;

        // Phase 3: Stream Central Directory entries one-by-one
        await foreach (ZipCentralDirectoryEntry cdEntry in reader.StreamCentralDirectoryAsync(eocd, cancellationToken)
            .ConfigureAwait(false))
        {
            // Convert to domain model
            ZipEntryInfo entryInfo = ConvertToZipEntryInfo(cdEntry);

            // Normalize path
            string normalizedPath = NormalizePath(cdEntry.FileName);

            // Add to flat dictionary
            entries[normalizedPath] = entryInfo;

            // Build directory tree incrementally
            AddToDirectoryTree(rootDirectory, normalizedPath, cdEntry.FileName, entryInfo);

            totalUncompressedSize += cdEntry.UncompressedSize;
            totalCompressedSize += cdEntry.CompressedSize;
            entryCount++;

            // Log progress for large archives
            if (entryCount % 10_000 == 0)
            {
                _logger?.LogDebug("Parsed {Count} entries...", entryCount);
            }
        }

        stopwatch.Stop();

        // Calculate estimated memory usage
        long estimatedMemory = BaseStructureOverhead + (entries.Count * BytesPerEntry);

        ArchiveStructure structure = new ArchiveStructure
        {
            ArchiveKey = archiveKey,
            AbsolutePath = absolutePath,
            Entries = entries,
            RootDirectory = rootDirectory,
            BuiltAt = _timeProvider.GetUtcNow(),
            IsZip64 = eocd.IsZip64,
            TotalUncompressedSize = totalUncompressedSize,
            TotalCompressedSize = totalCompressedSize,
            EstimatedMemoryBytes = estimatedMemory,
            Comment = eocd.Comment
        };

        _logger?.LogInformation(
            "Built structure for {ArchiveKey}: {EntryCount} entries in {ElapsedMs}ms, " +
            "estimated {MemoryMb:F2} MB",
            archiveKey,
            entries.Count,
            stopwatch.ElapsedMilliseconds,
            estimatedMemory / (1024.0 * 1024.0));

        return new CacheFactoryResult<ArchiveStructure>
        {
            Value = structure,
            SizeBytes = estimatedMemory,
            Metadata = new Dictionary<string, object>
            {
                ["EntryCount"] = entries.Count,
                ["IsZip64"] = eocd.IsZip64,
                ["TotalUncompressedSize"] = totalUncompressedSize,
                ["BuildTimeMs"] = stopwatch.ElapsedMilliseconds
            }
        };
    }

    /// <summary>
    /// Converts a Central Directory entry to the domain ZipEntryInfo.
    /// </summary>
    private static ZipEntryInfo ConvertToZipEntryInfo(ZipCentralDirectoryEntry cdEntry)
    {
        return new ZipEntryInfo
        {
            LocalHeaderOffset = cdEntry.LocalHeaderOffset,
            CompressedSize = cdEntry.CompressedSize,
            UncompressedSize = cdEntry.UncompressedSize,
            CompressionMethod = cdEntry.CompressionMethod,
            IsDirectory = cdEntry.IsDirectory,
            LastModified = cdEntry.LastModified,
            Attributes = ConvertAttributes(cdEntry),
            Crc32 = cdEntry.Crc32,
            IsEncrypted = cdEntry.IsEncrypted
        };
    }

    /// <summary>
    /// Converts ZIP external attributes to FileAttributes.
    /// </summary>
    private static FileAttributes ConvertAttributes(ZipCentralDirectoryEntry entry)
    {
        if (entry.IsDirectory)
            return FileAttributes.Directory;

        // Check if Unix (high byte of VersionMadeBy is 3)
        if (entry.HostOs == ZipConstants.OsUnix)
        {
            // Unix: permissions are in high 16 bits of external attributes
            // For now, just return Normal for files
            return FileAttributes.Normal;
        }

        // DOS/Windows: low byte contains DOS attributes
        uint dosAttrs = entry.ExternalFileAttributes & 0xFF;
        FileAttributes attrs = FileAttributes.Normal;

        if ((dosAttrs & ZipConstants.DosAttributeReadOnly) != 0)
            attrs |= FileAttributes.ReadOnly;
        if ((dosAttrs & ZipConstants.DosAttributeHidden) != 0)
            attrs |= FileAttributes.Hidden;
        if ((dosAttrs & ZipConstants.DosAttributeSystem) != 0)
            attrs |= FileAttributes.System;
        if ((dosAttrs & ZipConstants.DosAttributeArchive) != 0)
            attrs |= FileAttributes.Archive;

        return attrs;
    }

    /// <summary>
    /// Normalizes a ZIP path for consistent lookups.
    /// </summary>
    private static string NormalizePath(string fileName)
    {
        // Replace backslashes with forward slashes
        string normalized = fileName.Replace('\\', '/');

        // Remove leading slash if present
        if (normalized.StartsWith('/'))
            normalized = normalized[1..];

        // Remove trailing slash for files (keep for directories to distinguish)
        // Actually, for dictionary lookups we want consistent paths, so remove trailing slash
        if (normalized.EndsWith('/') && normalized.Length > 1)
            normalized = normalized[..^1];

        return normalized;
    }

    /// <summary>
    /// Adds an entry to the directory tree, creating parent directories as needed.
    /// </summary>
    private static void AddToDirectoryTree(
        DirectoryNode root,
        string normalizedPath,
        string originalPath,
        ZipEntryInfo entry)
    {
        string[] parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        DirectoryNode current = root;

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            bool isLastPart = i == parts.Length - 1;

            if (isLastPart && !entry.IsDirectory)
            {
                // This is a file - add to current directory
                current.AddFile(part, entry);
            }
            else
            {
                // This is a directory (explicit or implicit parent) - ensure it exists
                current = current.GetOrAddSubdirectory(part);
            }
        }
    }

    /// <inheritdoc />
    public bool Invalidate(string archiveKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveKey);

        // GenericCache doesn't expose direct removal by key
        // We would need to add this capability to GenericCache
        // For now, log a warning
        _logger?.LogWarning(
            "Invalidate called for {ArchiveKey} but direct removal is not supported. " +
            "Entry will expire based on TTL.",
            archiveKey);

        return false;
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (_cache is GenericCache<ArchiveStructure> genericCache)
        {
            genericCache.Clear();
            _logger?.LogInformation("Cache cleared");
        }
        else
        {
            _logger?.LogWarning("Clear() not supported for this cache implementation");
        }
    }

    /// <inheritdoc />
    public void EvictExpired()
    {
        _cache.EvictExpired();
    }

    /// <inheritdoc />
    public int CachedArchiveCount => _cache.EntryCount;

    /// <inheritdoc />
    public long EstimatedMemoryBytes => _cache.CurrentSizeBytes;

    /// <inheritdoc />
    public double HitRate => _cache.HitRate;

    /// <inheritdoc />
    public long HitCount
    {
        get
        {
            // Calculate from cache hit rate and our miss count
            double cacheHitRate = _cache.HitRate;
            long totalMisses = Interlocked.Read(ref _missCount);

            // hits / (hits + misses) = hitRate
            // hits = hitRate * (hits + misses)
            // hits = hitRate * hits + hitRate * misses
            // hits - hitRate * hits = hitRate * misses
            // hits * (1 - hitRate) = hitRate * misses
            // hits = hitRate * misses / (1 - hitRate)

            if (Math.Abs(cacheHitRate - 1.0) < 0.0001) // Nearly 100% hit rate
                return _cache.EntryCount > 0 ? long.MaxValue : 0;

            return (long)(cacheHitRate * totalMisses / (1 - cacheHitRate));
        }
    }

    /// <inheritdoc />
    public long MissCount => Interlocked.Read(ref _missCount);
}
