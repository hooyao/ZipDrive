using System.Diagnostics;
using System.Text;
using KTrie;
using Microsoft.Extensions.Logging;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip.Formats;

namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// ZIP-specific structure builder. Parses the Central Directory to produce an ArchiveStructure
/// and populates ZipFormatMetadataStore with format-specific extraction metadata as a side effect.
/// </summary>
public sealed class ZipStructureBuilder : IArchiveStructureBuilder, IArchiveMetadataCleanup
{
    private const long BytesPerEntry = 114;
    private const long BaseStructureOverhead = 1024;

    private readonly IZipReaderFactory _zipReaderFactory;
    private readonly IFilenameEncodingDetector _encodingDetector;
    private readonly ZipFormatMetadataStore _metadataStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ZipStructureBuilder> _logger;

    public string FormatId => "zip";
    public IReadOnlyList<string> SupportedExtensions => [".zip"];

    public ZipStructureBuilder(
        IZipReaderFactory zipReaderFactory,
        IFilenameEncodingDetector encodingDetector,
        ZipFormatMetadataStore metadataStore,
        TimeProvider timeProvider,
        ILogger<ZipStructureBuilder> logger)
    {
        _zipReaderFactory = zipReaderFactory;
        _encodingDetector = encodingDetector;
        _metadataStore = metadataStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<ArchiveStructure> BuildAsync(
        string archiveKey,
        string absolutePath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Building ZIP structure for {ArchiveKey} at {Path}", archiveKey, absolutePath);
        Stopwatch stopwatch = Stopwatch.StartNew();

        await using IZipReader reader = _zipReaderFactory.Create(absolutePath);
        ZipEocd eocd = await reader.ReadEocdAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "EOCD: {EntryCount} entries, CD at {Offset}, size {Size}, ZIP64={IsZip64}",
            eocd.EntryCount, eocd.CentralDirectoryOffset, eocd.CentralDirectorySize, eocd.IsZip64);

        // Phase 1: Stream Central Directory, partition by UTF-8 flag
        TrieDictionary<ArchiveEntryInfo> trie = new();
        HashSet<string> directoriesSeen = new(StringComparer.Ordinal);
        List<(string InternalPath, ZipEntryInfo Entry)> metadataEntries = new();

        long totalUncompressedSize = 0;
        long totalCompressedSize = 0;
        int entryCount = 0;

        List<(ZipCentralDirectoryEntry CdEntry, ZipEntryInfo Info)>? nonUtf8Buffer = null;

        await foreach (ZipCentralDirectoryEntry cdEntry in reader
                           .StreamCentralDirectoryAsync(eocd, cancellationToken)
                           .ConfigureAwait(false))
        {
            ZipEntryInfo entryInfo = ConvertToZipEntryInfo(cdEntry);

            if (cdEntry.IsUtf8)
            {
                string normalizedPath = NormalizePath(cdEntry.DecodeFileName());
                InsertEntry(trie, directoriesSeen, entryInfo, normalizedPath, _timeProvider);

                if (!entryInfo.IsDirectory)
                    metadataEntries.Add((normalizedPath, entryInfo));
            }
            else
            {
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
                _logger.LogDebug("Parsed {Count} entries...", entryCount);
        }

        // Phase 2 & 3: Detect encoding and insert buffered non-UTF8 entries
        if (nonUtf8Buffer is { Count: > 0 })
            DecodeAndInsertNonUtf8Entries(trie, directoriesSeen, nonUtf8Buffer, metadataEntries);

        // Side effect: populate format metadata store for extractor and prefetch.
        // Keyed by archiveKey (virtual path) only — extractor receives archiveKey via
        // the updated IArchiveEntryExtractor.ExtractAsync signature.
        _metadataStore.Populate(archiveKey, metadataEntries);

        stopwatch.Stop();
        long estimatedMemory = BaseStructureOverhead + (trie.Count * BytesPerEntry);

        ArchiveStructure structure = new()
        {
            ArchiveKey = archiveKey,
            AbsolutePath = absolutePath,
            Entries = trie,
            BuiltAt = _timeProvider.GetUtcNow(),
            FormatId = "zip",
            TotalUncompressedSize = totalUncompressedSize,
            TotalCompressedSize = totalCompressedSize,
            EstimatedMemoryBytes = estimatedMemory,
            Comment = eocd.Comment
        };

        _logger.LogInformation(
            "Built ZIP structure for {ArchiveKey}: {EntryCount} entries in {ElapsedMs}ms, estimated {MemoryMb:F2} MB",
            archiveKey, trie.Count, stopwatch.ElapsedMilliseconds, estimatedMemory / (1024.0 * 1024.0));

        return structure;
    }

    public void CleanupArchive(string archiveKey) => _metadataStore.Remove(archiveKey);

    // ── Helpers (relocated from ArchiveStructureCache) ──────────────────────

    private static void InsertEntry(
        TrieDictionary<ArchiveEntryInfo> trie,
        HashSet<string> directoriesSeen,
        ZipEntryInfo entryInfo,
        string normalizedPath,
        TimeProvider timeProvider)
    {
        ArchiveEntryInfo archiveEntry = ToArchiveEntryInfo(entryInfo);
        if (entryInfo.IsDirectory)
        {
            string dirPath = normalizedPath.EndsWith('/') ? normalizedPath : normalizedPath + "/";
            trie[dirPath] = archiveEntry;
            directoriesSeen.Add(dirPath);
        }
        else
        {
            string filePath = normalizedPath.TrimEnd('/');
            trie[filePath] = archiveEntry;
            EnsureParentDirectories(trie, filePath, directoriesSeen, timeProvider);
        }
    }

    private void DecodeAndInsertNonUtf8Entries(
        TrieDictionary<ArchiveEntryInfo> trie,
        HashSet<string> directoriesSeen,
        List<(ZipCentralDirectoryEntry CdEntry, ZipEntryInfo Info)> buffer,
        List<(string InternalPath, ZipEntryInfo Entry)> metadataEntries)
    {
        List<byte[]> allBytes = buffer.Select(e => e.CdEntry.FileNameBytes).ToList();
        Encoding? archiveEncoding = _encodingDetector.DetectArchiveEncoding(allBytes);

        if (archiveEncoding != null)
        {
            foreach ((ZipCentralDirectoryEntry cdEntry, ZipEntryInfo info) in buffer)
            {
                string path = NormalizePath(cdEntry.DecodeFileName(archiveEncoding));
                InsertEntry(trie, directoriesSeen, info, path, _timeProvider);
                if (!info.IsDirectory)
                    metadataEntries.Add((path.TrimEnd('/'), info));
            }
        }
        else
        {
            _logger.LogInformation(
                "Archive-level encoding detection inconclusive, using per-entry detection for {Count} entries",
                buffer.Count);

            foreach ((ZipCentralDirectoryEntry cdEntry, ZipEntryInfo info) in buffer)
            {
                Encoding encoding = _encodingDetector.ResolveEntryEncoding(cdEntry.FileNameBytes);
                string path = NormalizePath(cdEntry.DecodeFileName(encoding));
                InsertEntry(trie, directoriesSeen, info, path, _timeProvider);
                if (!info.IsDirectory)
                    metadataEntries.Add((path.TrimEnd('/'), info));
            }
        }
    }

    /// <summary>
    /// Ensures parent directories exist during incremental trie insertion.
    /// Uses TimeProvider for deterministic testing. Intentionally NOT using
    /// DirectorySynthesizer (which is a post-pass approach for completed tries).
    /// </summary>
    private static void EnsureParentDirectories(
        TrieDictionary<ArchiveEntryInfo> trie,
        string filePath,
        HashSet<string> seen,
        TimeProvider timeProvider)
    {
        int lastSlash = filePath.LastIndexOf('/');
        while (lastSlash > 0)
        {
            string dirPath = filePath[..(lastSlash + 1)];

            if (!seen.Add(dirPath))
                break;

            if (!trie.ContainsKey(dirPath))
            {
                trie[dirPath] = new ArchiveEntryInfo
                {
                    UncompressedSize = 0,
                    IsDirectory = true,
                    LastModified = timeProvider.GetUtcNow().UtcDateTime,
                    Attributes = FileAttributes.Directory,
                };
            }

            lastSlash = filePath.LastIndexOf('/', lastSlash - 1);
        }
    }

    private static ArchiveEntryInfo ToArchiveEntryInfo(ZipEntryInfo zip) => new()
    {
        UncompressedSize = zip.UncompressedSize,
        IsDirectory = zip.IsDirectory,
        LastModified = zip.LastModified,
        Attributes = zip.Attributes,
        IsEncrypted = zip.IsEncrypted,
        Checksum = zip.Crc32
    };

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

    internal static string NormalizePath(string fileName)
    {
        string normalized = fileName.Replace('\\', '/');
        if (normalized.StartsWith('/'))
            normalized = normalized[1..];
        return normalized;
    }
}
