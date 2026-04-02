using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// ZIP-specific prefetch strategy. Reads a contiguous byte span from the archive file,
/// parsing ZIP local headers inline, decompressing wanted entries, and warming them
/// into the file content cache. Uses SpanSelector for window selection.
/// </summary>
public sealed class ZipPrefetchStrategy : IPrefetchStrategy
{
    private const int DiscardBufferSize = 64 * 1024;
    private const int LocalHeaderFixedSize = 30;
    private const int FileNameLengthOffset = 26;

    private static readonly Meter Meter = new("ZipDrive.Caching");

    private static readonly Counter<long> FilesWarmed =
        Meter.CreateCounter<long>("prefetch.files_warmed",
            unit: "{file}", description: "Number of sibling files warmed by prefetch");

    private static readonly Counter<long> BytesRead =
        Meter.CreateCounter<long>("prefetch.bytes_read",
            unit: "By", description: "Total bytes read during prefetch spans");

    private static readonly Histogram<double> SpanReadDuration =
        Meter.CreateHistogram<double>("prefetch.span_read_duration",
            unit: "ms", description: "Duration of a sequential span read during prefetch");

    private readonly ZipFormatMetadataStore _metadataStore;
    private readonly ILogger<ZipPrefetchStrategy> _logger;

    public string FormatId => "zip";

    public ZipPrefetchStrategy(
        ZipFormatMetadataStore metadataStore,
        ILogger<ZipPrefetchStrategy> logger)
    {
        _metadataStore = metadataStore;
        _logger = logger;
    }

    public async Task PrefetchAsync(
        string archivePath,
        ArchiveStructure structure,
        string dirInternalPath,
        ArchiveEntryInfo? triggerEntry,
        string? triggerInternalPath,
        IFileContentCache contentCache,
        PrefetchOptions options,
        CancellationToken cancellationToken = default)
    {
        // Get ZIP metadata for entries in this directory
        IReadOnlyDictionary<string, ZipEntryInfo>? zipEntries = _metadataStore.GetArchiveEntries(structure.ArchiveKey);
        if (zipEntries == null) return;

        // Build candidate list: non-directory files below size threshold
        long sizeThreshold = options.FileSizeThresholdBytes;
        string dirPrefix = string.IsNullOrEmpty(dirInternalPath) ? "" : dirInternalPath + "/";

        // Build from ZipFormatMetadataStore directly — avoids depending on
        // ArchiveStructure's entry type (ZipEntryInfo now, ArchiveEntryInfo after Phase 4).
        List<(string InternalPath, ZipEntryInfo ZipEntry)> allItems = [];
        foreach (var (path, zipEntry) in zipEntries)
        {
            if (zipEntry.IsDirectory || zipEntry.UncompressedSize > sizeThreshold) continue;

            // Check if entry is in this directory (direct child)
            if (!string.IsNullOrEmpty(dirPrefix) && !path.StartsWith(dirPrefix, StringComparison.Ordinal))
                continue;
            string remainder = string.IsNullOrEmpty(dirPrefix) ? path : path[dirPrefix.Length..];
            if (remainder.Contains('/')) continue; // nested, not direct child

            allItems.Add((path, zipEntry));
        }

        if (allItems.Count == 0) return;

        // Determine trigger's ZIP metadata for centering
        ZipEntryInfo? triggerZip = null;
        if (triggerInternalPath != null && zipEntries.TryGetValue(triggerInternalPath, out var tz))
        {
            triggerZip = tz;
        }
        else if (triggerEntry.HasValue)
        {
            // Fallback: match by UncompressedSize + LastModified (legacy path)
            triggerZip = allItems
                .Where(x => x.ZipEntry.UncompressedSize == triggerEntry.Value.UncompressedSize
                         && x.ZipEntry.LastModified == triggerEntry.Value.LastModified)
                .Select(x => (ZipEntryInfo?)x.ZipEntry)
                .FirstOrDefault();
        }

        // Apply MaxDirectoryFiles cap: keep entries nearest to trigger by offset
        long pivotOffset = triggerZip?.LocalHeaderOffset ?? allItems[0].ZipEntry.LocalHeaderOffset;
        IEnumerable<(string InternalPath, ZipEntryInfo ZipEntry)> capped = allItems.Count > options.MaxDirectoryFiles
            ? allItems.OrderBy(x => Math.Abs(x.ZipEntry.LocalHeaderOffset - pivotOffset)).Take(options.MaxDirectoryFiles)
            : allItems;

        // Exclude trigger from span candidates
        List<ZipEntryInfo> candidates = capped
            .Where(x => triggerZip == null || x.ZipEntry.LocalHeaderOffset != triggerZip.Value.LocalHeaderOffset)
            .Select(x => x.ZipEntry)
            .ToList();

        if (candidates.Count == 0) return;

        ZipEntryInfo anchor = triggerZip ?? candidates[0];

        ZipPrefetchPlan plan = SpanSelector.Select(
            candidates, anchor, options.MaxFiles, options.FillRatioThreshold);

        if (plan.IsEmpty) return;

        // Build lookup: LocalHeaderOffset → (internalPath, zipEntry)
        Dictionary<long, (string InternalPath, ZipEntryInfo ZipEntry)> wantedByOffset =
            allItems
                .Where(x => plan.Entries.Any(e => e.LocalHeaderOffset == x.ZipEntry.LocalHeaderOffset))
                .ToDictionary(x => x.ZipEntry.LocalHeaderOffset);

        // Skip entries already in cache
        int totalWanted = wantedByOffset.Count;
        wantedByOffset = wantedByOffset
            .Where(kv => !contentCache.ContainsKey($"{structure.ArchiveKey}:{kv.Value.InternalPath}"))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (wantedByOffset.Count == 0)
        {
            _logger.LogInformation(
                "Prefetch skipped (all {Count} entries already cached): {Archive}/{Dir}",
                plan.Entries.Count, structure.ArchiveKey, dirInternalPath);
            return;
        }

        // Sequential span read
        IOrderedEnumerable<ZipEntryInfo> ordered = plan.Entries.OrderBy(e => e.LocalHeaderOffset);
        long bytesRead = 0;
        int filesWarmed = 0;
        var sw = Stopwatch.StartNew();

        await using FileStream zipStream = new(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);

        zipStream.Seek(plan.SpanStart, SeekOrigin.Begin);
        long currentPos = plan.SpanStart;

        byte[] discardBuffer = ArrayPool<byte>.Shared.Rent(DiscardBufferSize);
        byte[] headerBuffer = new byte[LocalHeaderFixedSize];

        try
        {
            foreach (ZipEntryInfo wantedEntry in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long gap = wantedEntry.LocalHeaderOffset - currentPos;
                if (gap < 0) continue;

                if (gap > 0)
                {
                    long discarded = await DiscardBytesAsync(zipStream, gap, discardBuffer, cancellationToken).ConfigureAwait(false);
                    bytesRead += discarded;
                    currentPos += discarded;
                }

                int headerRead = await ReadExactAsync(zipStream, headerBuffer, 0, LocalHeaderFixedSize, cancellationToken).ConfigureAwait(false);
                if (headerRead < LocalHeaderFixedSize) break;
                currentPos += headerRead;
                bytesRead += headerRead;

                ushort fileNameLen = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(FileNameLengthOffset, 2));
                ushort extraLen = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(28, 2));
                int variableHeaderLen = fileNameLen + extraLen;

                if (variableHeaderLen > 0)
                {
                    long skipped = await DiscardBytesAsync(zipStream, variableHeaderLen, discardBuffer, cancellationToken).ConfigureAwait(false);
                    currentPos += skipped;
                    bytesRead += skipped;
                }

                long compressedSize = wantedEntry.CompressedSize;

                if (!wantedByOffset.TryGetValue(wantedEntry.LocalHeaderOffset, out var wanted))
                {
                    long discarded = await DiscardBytesAsync(zipStream, compressedSize, discardBuffer, cancellationToken).ConfigureAwait(false);
                    currentPos += discarded;
                    bytesRead += discarded;
                    continue;
                }

                if (compressedSize > int.MaxValue)
                {
                    // Skip prefetching entries too large for memory-based sequential read
                    long discarded = await DiscardBytesAsync(zipStream, compressedSize, discardBuffer, cancellationToken).ConfigureAwait(false);
                    currentPos += discarded;
                    bytesRead += discarded;
                    continue;
                }

                string cacheKey = $"{structure.ArchiveKey}:{wanted.InternalPath}";
                byte[] compressedBuffer = ArrayPool<byte>.Shared.Rent((int)compressedSize);
                try
                {
                    int compRead = await ReadExactAsync(zipStream, compressedBuffer, 0, (int)compressedSize, cancellationToken).ConfigureAwait(false);
                    currentPos += compRead;
                    bytesRead += compRead;

                    MemoryStream decompressed = new((int)wantedEntry.UncompressedSize);
                    ushort compressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(8, 2));

                    if (compressionMethod == 0) // Store
                    {
                        await decompressed.WriteAsync(compressedBuffer.AsMemory(0, compRead), cancellationToken).ConfigureAwait(false);
                    }
                    else if (compressionMethod == 8) // Deflate
                    {
                        await using var deflate = new DeflateStream(
                            new MemoryStream(compressedBuffer, 0, compRead, writable: false),
                            CompressionMode.Decompress, leaveOpen: false);
                        await deflate.CopyToAsync(decompressed, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        continue; // Unsupported compression
                    }

                    decompressed.Position = 0;

                    ArchiveEntryInfo archiveEntry = new()
                    {
                        UncompressedSize = wantedEntry.UncompressedSize,
                        IsDirectory = wantedEntry.IsDirectory,
                        LastModified = wantedEntry.LastModified,
                        Attributes = wantedEntry.Attributes,
                        IsEncrypted = wantedEntry.IsEncrypted,
                        Checksum = wantedEntry.Crc32
                    };
                    await contentCache.WarmAsync(archiveEntry, cacheKey, decompressed, cancellationToken).ConfigureAwait(false);
                    filesWarmed++;
                    _logger.LogDebug("Prefetch warmed: {CacheKey} ({Bytes:N0} bytes)", cacheKey, wantedEntry.UncompressedSize);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(compressedBuffer);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(discardBuffer);
        }

        sw.Stop();
        FilesWarmed.Add(filesWarmed);
        BytesRead.Add(bytesRead);
        SpanReadDuration.Record(sw.Elapsed.TotalMilliseconds);

        _logger.LogInformation(
            "Prefetch complete: {Archive}/{Dir} — {Files}/{Candidates} files warmed, {Bytes:N0} bytes read in {Ms:F1} ms",
            structure.ArchiveKey, dirInternalPath, filesWarmed, totalWanted, bytesRead, sw.Elapsed.TotalMilliseconds);
    }

    // ── I/O helpers ─────────────────────────────────────────────────────────

    private static async Task<long> DiscardBytesAsync(
        Stream stream, long count, byte[] buffer, CancellationToken ct)
    {
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(remaining, buffer.Length);
            int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            remaining -= read;
        }
        return count - remaining;
    }

    private static async Task<int> ReadExactAsync(
        Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }
}
