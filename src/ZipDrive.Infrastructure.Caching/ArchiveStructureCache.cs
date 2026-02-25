using System.Diagnostics;
using System.Text;
using KTrie;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip;
using ZipDrive.Infrastructure.Archives.Zip.Formats;

namespace ZipDrive.Infrastructure.Caching;

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

    private readonly IArchiveStructureStore _cache;
    private readonly IZipReaderFactory _zipReaderFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _defaultTtl;
    private readonly IFilenameEncodingDetector _encodingDetector;
    private readonly ILogger<ArchiveStructureCache> _logger;

    private long _missCount;

    public ArchiveStructureCache(
        IArchiveStructureStore cache,
        IZipReaderFactory zipReaderFactory,
        TimeProvider timeProvider,
        IOptions<CacheOptions> cacheOptions,
        ILogger<ArchiveStructureCache> logger,
        IFilenameEncodingDetector encodingDetector)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _zipReaderFactory = zipReaderFactory ?? throw new ArgumentNullException(nameof(zipReaderFactory));
        _timeProvider = timeProvider;
        _defaultTtl = cacheOptions.Value.DefaultTtl;
        _encodingDetector = encodingDetector ?? throw new ArgumentNullException(nameof(encodingDetector));
        _logger = logger;

        _logger.LogInformation(
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

        _logger.LogDebug("GetOrBuildAsync: {ArchiveKey} at {Path}", archiveKey, absolutePath);

        ICacheHandle<ArchiveStructure> handle = await _cache.BorrowAsync(
            archiveKey,
            _defaultTtl,
            ct => BuildStructureAsync(archiveKey, absolutePath, ct),
            cancellationToken).ConfigureAwait(false);

        try
        {
            ArchiveStructure structure = handle.Value;

            _logger.LogDebug(
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
        _logger.LogInformation("Building structure for {ArchiveKey} at {Path}", archiveKey, absolutePath);
        Interlocked.Increment(ref _missCount);

        Stopwatch stopwatch = Stopwatch.StartNew();

        // Phase 1: Read EOCD
        await using (IZipReader reader = _zipReaderFactory.Create(absolutePath))
        {
            ZipEocd eocd = await reader.ReadEocdAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "EOCD: {EntryCount} entries, CD at {Offset}, size {Size}, ZIP64={IsZip64}",
                eocd.EntryCount, eocd.CentralDirectoryOffset, eocd.CentralDirectorySize, eocd.IsZip64);

            // Phase 1: Stream Central Directory, partition by UTF-8 flag
            TrieDictionary<ZipEntryInfo> trie = new();
            HashSet<string> directoriesSeen = new(StringComparer.Ordinal);

            long totalUncompressedSize = 0;
            long totalCompressedSize = 0;
            int entryCount = 0;

            // Buffer for non-UTF8 entries (deferred until encoding detection)
            List<(ZipCentralDirectoryEntry CdEntry, ZipEntryInfo Info)>? nonUtf8Buffer = null;

            await foreach (ZipCentralDirectoryEntry cdEntry in reader
                               .StreamCentralDirectoryAsync(eocd, cancellationToken)
                               .ConfigureAwait(false))
            {
                ZipEntryInfo entryInfo = ConvertToZipEntryInfo(cdEntry);

                if (cdEntry.IsUtf8)
                {
                    // UTF-8 entries: encoding is known, insert immediately
                    string normalizedPath = NormalizePath(cdEntry.DecodeFileName());
                    InsertEntry(trie, directoriesSeen, entryInfo, normalizedPath, _timeProvider);
                }
                else
                {
                    // Non-UTF8 entries: buffer for batch detection
                    nonUtf8Buffer ??= new();
                    nonUtf8Buffer.Add((cdEntry, entryInfo));
                }

                if (!entryInfo.IsDirectory)
                {
                    totalUncompressedSize += cdEntry.UncompressedSize;
                    totalCompressedSize += cdEntry.CompressedSize;
                }

                entryCount++;

                if (entryCount % 10_000 == 0)
                {
                    _logger.LogDebug("Parsed {Count} entries...", entryCount);
                }
            }

            // Phase 2 & 3: Detect encoding and insert buffered non-UTF8 entries
            if (nonUtf8Buffer is { Count: > 0 })
            {
                DecodeAndInsertNonUtf8Entries(trie, directoriesSeen, nonUtf8Buffer);
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

            _logger.LogInformation(
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
    }

    /// <summary>
    /// Inserts an entry into the trie with appropriate key formatting.
    /// </summary>
    private static void InsertEntry(
        TrieDictionary<ZipEntryInfo> trie,
        HashSet<string> directoriesSeen,
        ZipEntryInfo entryInfo,
        string normalizedPath,
        TimeProvider timeProvider)
    {
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
            EnsureParentDirectories(trie, filePath, directoriesSeen, timeProvider);
        }
    }

    /// <summary>
    /// Detects encoding for buffered non-UTF8 entries, decodes filenames, and inserts into trie.
    /// Uses two-level detection: per-archive fast path, per-entry fallback for mixed-encoding archives.
    /// Encoding decisions (including fallback) are fully owned by the detector.
    /// </summary>
    private void DecodeAndInsertNonUtf8Entries(
        TrieDictionary<ZipEntryInfo> trie,
        HashSet<string> directoriesSeen,
        List<(ZipCentralDirectoryEntry CdEntry, ZipEntryInfo Info)> buffer)
    {
        // Phase 2: Per-archive detection
        List<byte[]> allBytes = buffer.Select(e => e.CdEntry.FileNameBytes).ToList();
        Encoding? archiveEncoding = _encodingDetector.DetectArchiveEncoding(allBytes);

        if (archiveEncoding != null)
        {
            // Fast path: archive-level detection succeeded, apply to all entries
            foreach ((ZipCentralDirectoryEntry cdEntry, ZipEntryInfo info) in buffer)
            {
                string path = NormalizePath(cdEntry.DecodeFileName(archiveEncoding));
                InsertEntry(trie, directoriesSeen, info, path, _timeProvider);
            }
        }
        else
        {
            _logger.LogInformation(
                "Archive-level encoding detection inconclusive, using per-entry detection for {Count} entries",
                buffer.Count);

            // Slow path: per-entry detection (detector handles fallback internally)
            foreach ((ZipCentralDirectoryEntry cdEntry, ZipEntryInfo info) in buffer)
            {
                Encoding encoding = _encodingDetector.ResolveEntryEncoding(cdEntry.FileNameBytes);
                string path = NormalizePath(cdEntry.DecodeFileName(encoding));
                InsertEntry(trie, directoriesSeen, info, path, _timeProvider);
            }
        }
    }

    /// <summary>
    /// Ensures all parent directories exist for a file path.
    /// Creates synthetic directory entries for any missing parent segments.
    /// </summary>
    private static void EnsureParentDirectories(
        TrieDictionary<ZipEntryInfo> trie,
        string filePath,
        HashSet<string> seen,
        TimeProvider timeProvider)
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
                    LastModified = timeProvider.GetUtcNow().UtcDateTime,
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

        _logger.LogWarning(
            "Invalidate called for {ArchiveKey} but direct removal is not supported. " +
            "Entry will expire based on TTL.",
            archiveKey);

        return false;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _cache.Clear();
        _logger.LogInformation("Cache cleared");
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
