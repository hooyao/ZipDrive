using System.Diagnostics;
using KTrie;
using Microsoft.Extensions.Logging;
using ZipDriveV3.Domain.Abstractions;
using ZipDriveV3.Domain.Models;
using ZipDriveV3.Infrastructure.Archives.Zip;
using ZipDriveV3.Infrastructure.Archives.Zip.Formats;

namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// Caches parsed ZIP archive structures using GenericCache with ObjectStorageStrategy.
/// Uses <see cref="IZipReader.StreamCentralDirectoryAsync"/> for memory-efficient parsing.
/// Builds TrieDictionary-based ArchiveStructure with parent directory synthesis.
/// </summary>
public sealed class ArchiveStructureCache : IArchiveStructureCache
{
    /// <summary>
    /// Estimated memory overhead per ZIP entry in bytes.
    /// Includes: ZipEntryInfo struct (~48) + filename string (~50 avg) + trie node overhead (~16).
    /// </summary>
    private const long BytesPerEntry = 114;

    /// <summary>
    /// Base overhead per ArchiveStructure object in bytes.
    /// </summary>
    private const long BaseStructureOverhead = 1024;

    private readonly ICache<ArchiveStructure> _cache;
    private readonly Func<string, IZipReader> _zipReaderFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _defaultTtl;
    private readonly ILogger<ArchiveStructureCache>? _logger;

    private long _missCount;

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

        ICacheHandle<ArchiveStructure> handle = await _cache.BorrowAsync(
            archiveKey,
            _defaultTtl,
            ct => BuildStructureAsync(archiveKey, absolutePath, ct),
            cancellationToken).ConfigureAwait(false);

        try
        {
            ArchiveStructure structure = handle.Value;

            _logger?.LogDebug(
                "Returning structure for {ArchiveKey}: {EntryCount} entries",
                archiveKey,
                structure.EntryCount);

            return structure;
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>
    /// Builds the archive structure by streaming the Central Directory into a trie.
    /// Synthesizes parent directory entries for paths missing explicit directory entries.
    /// </summary>
    private async Task<CacheFactoryResult<ArchiveStructure>> BuildStructureAsync(
        string archiveKey,
        string absolutePath,
        CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Building structure for {ArchiveKey} at {Path}", archiveKey, absolutePath);
        Interlocked.Increment(ref _missCount);

        Stopwatch stopwatch = Stopwatch.StartNew();

        await using IZipReader reader = _zipReaderFactory(absolutePath);

        // Phase 1: Read EOCD
        ZipEocd eocd = await reader.ReadEocdAsync(cancellationToken).ConfigureAwait(false);

        _logger?.LogDebug(
            "EOCD: {EntryCount} entries, CD at {Offset}, size {Size}, ZIP64={IsZip64}",
            eocd.EntryCount, eocd.CentralDirectoryOffset, eocd.CentralDirectorySize, eocd.IsZip64);

        // Phase 2: Build trie from Central Directory entries
        TrieDictionary<ZipEntryInfo> trie = new();
        HashSet<string> directoriesSeen = new(StringComparer.Ordinal);

        long totalUncompressedSize = 0;
        long totalCompressedSize = 0;
        int entryCount = 0;

        await foreach (ZipCentralDirectoryEntry cdEntry in reader.StreamCentralDirectoryAsync(eocd, cancellationToken)
            .ConfigureAwait(false))
        {
            ZipEntryInfo entryInfo = ConvertToZipEntryInfo(cdEntry);
            string normalizedPath = NormalizePath(cdEntry.FileName);

            if (entryInfo.IsDirectory)
            {
                string dirPath = normalizedPath.EndsWith('/') ? normalizedPath : normalizedPath + "/";
                trie[dirPath] = entryInfo;
                directoriesSeen.Add(dirPath);
            }
            else
            {
                string filePath = normalizedPath.TrimEnd('/');
                trie[filePath] = entryInfo;
                totalUncompressedSize += cdEntry.UncompressedSize;
                totalCompressedSize += cdEntry.CompressedSize;

                EnsureParentDirectories(trie, filePath, directoriesSeen);
            }

            entryCount++;

            if (entryCount % 10_000 == 0)
            {
                _logger?.LogDebug("Parsed {Count} entries...", entryCount);
            }
        }

        stopwatch.Stop();

        long estimatedMemory = BaseStructureOverhead + (trie.Count * BytesPerEntry);

        ArchiveStructure structure = new ArchiveStructure
        {
            ArchiveKey = archiveKey,
            AbsolutePath = absolutePath,
            Entries = trie,
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
            trie.Count,
            stopwatch.ElapsedMilliseconds,
            estimatedMemory / (1024.0 * 1024.0));

        return new CacheFactoryResult<ArchiveStructure>
        {
            Value = structure,
            SizeBytes = estimatedMemory,
            Metadata = new Dictionary<string, object>
            {
                ["EntryCount"] = trie.Count,
                ["IsZip64"] = eocd.IsZip64,
                ["TotalUncompressedSize"] = totalUncompressedSize,
                ["BuildTimeMs"] = stopwatch.ElapsedMilliseconds
            }
        };
    }

    /// <summary>
    /// Ensures all parent directories exist for a file path.
    /// Creates synthetic directory entries for any missing parent segments.
    /// </summary>
    private static void EnsureParentDirectories(
        TrieDictionary<ZipEntryInfo> trie,
        string filePath,
        HashSet<string> seen)
    {
        int lastSlash = filePath.LastIndexOf('/');
        while (lastSlash > 0)
        {
            string dirPath = filePath[..(lastSlash + 1)]; // Include trailing /

            if (!seen.Add(dirPath))
                break; // Already processed this and all parents

            if (!trie.ContainsKey(dirPath))
            {
                trie[dirPath] = new ZipEntryInfo
                {
                    LocalHeaderOffset = 0,
                    CompressedSize = 0,
                    UncompressedSize = 0,
                    CompressionMethod = 0,
                    IsDirectory = true,
                    LastModified = DateTime.UtcNow,
                    Attributes = FileAttributes.Directory,
                    Crc32 = 0
                };
            }

            lastSlash = filePath.LastIndexOf('/', lastSlash - 1);
        }
    }

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

    private static FileAttributes ConvertAttributes(ZipCentralDirectoryEntry entry)
    {
        if (entry.IsDirectory)
            return FileAttributes.Directory;

        if (entry.HostOs == ZipConstants.OsUnix)
            return FileAttributes.Normal;

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

    private static string NormalizePath(string fileName)
    {
        string normalized = fileName.Replace('\\', '/');

        if (normalized.StartsWith('/'))
            normalized = normalized[1..];

        return normalized;
    }

    /// <inheritdoc />
    public bool Invalidate(string archiveKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveKey);

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
            double cacheHitRate = _cache.HitRate;
            long totalMisses = Interlocked.Read(ref _missCount);

            if (Math.Abs(cacheHitRate - 1.0) < 0.0001)
                return _cache.EntryCount > 0 ? long.MaxValue : 0;

            return (long)(cacheHitRate * totalMisses / (1 - cacheHitRate));
        }
    }

    /// <inheritdoc />
    public long MissCount => Interlocked.Read(ref _missCount);
}
