using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Exceptions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Application.Services;

/// <summary>
/// Platform-independent virtual file system that mounts ZIP archives as folders.
/// Orchestrates archive trie, structure cache, file content cache, and ZIP reader.
/// </summary>
public sealed class ZipVirtualFileSystem : IVirtualFileSystem, IArchiveManager
{
    private readonly IArchiveTrie _archiveTrie;
    private readonly IArchiveStructureCache _structureCache;
    private readonly IFileContentCache _fileContentCache;
    private readonly IArchiveDiscovery _discovery;
    private readonly IPathResolver _pathResolver;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly MountSettings _mountSettings;
    private readonly PrefetchOptions _prefetchOptions;
    private readonly ILogger<ZipVirtualFileSystem> _logger;
    private string _volumeLabel = "ZipDrive";
    private long _totalArchiveBytes;

    // NTFS volume labels are limited to 32 characters
    private const int MaxVolumeLabelLength = 32;

    // Per-archive ref counting for drain-before-removal
    private readonly ConcurrentDictionary<string, ArchiveNode> _archiveNodes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    // Per-directory in-flight guard: prevents duplicate concurrent sequential scans.
    // Key = "archiveVirtualPath:dirInternalPath"
    private readonly ConcurrentDictionary<string, byte> _prefetchInFlight = new(StringComparer.OrdinalIgnoreCase);

    // Discard buffer for hole bytes during sequential scan (64 KB)
    private const int DiscardBufferSize = 64 * 1024;

    // Fixed size of a ZIP local file header (excluding variable-length filename and extra field)
    private const int LocalHeaderFixedSize = 30;
    // Offset of FileNameLength within the fixed header
    private const int FileNameLengthOffset = 26;

    public ZipVirtualFileSystem(
        IArchiveTrie archiveTrie,
        IArchiveStructureCache structureCache,
        IFileContentCache fileContentCache,
        IArchiveDiscovery discovery,
        IPathResolver pathResolver,
        IHostApplicationLifetime appLifetime,
        IOptions<MountSettings> mountSettings,
        IOptions<PrefetchOptions> prefetchOptions,
        ILogger<ZipVirtualFileSystem> logger)
    {
        _archiveTrie = archiveTrie;
        _structureCache = structureCache;
        _fileContentCache = fileContentCache;
        _discovery = discovery;
        _pathResolver = pathResolver;
        _appLifetime = appLifetime;
        _mountSettings = mountSettings.Value;
        _prefetchOptions = prefetchOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsMounted { get; private set; }

    /// <inheritdoc />
    public event EventHandler<bool>? MountStateChanged;

    /// <inheritdoc />
    public async Task MountAsync(VfsMountOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger.LogInformation("Mounting VFS from {RootPath} with depth {Depth}",
            options.RootPath, options.MaxDiscoveryDepth);

        IReadOnlyList<ArchiveDescriptor> archives = await _discovery.DiscoverAsync(
            options.RootPath,
            options.MaxDiscoveryDepth,
            cancellationToken).ConfigureAwait(false);

        foreach (ArchiveDescriptor archive in archives)
        {
            await AddArchiveAsync(archive, cancellationToken).ConfigureAwait(false);
        }

        string label = _mountSettings.UseFolderNameAsVolumeLabel
            ? Path.GetFileName(options.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "ZipDrive"
            : "ZipDrive";
        if (label.Length > MaxVolumeLabelLength)
        {
            _logger.LogWarning("Volume label truncated from {Length} to {Max} chars: \"{Original}\"",
                label.Length, MaxVolumeLabelLength, label);
            label = label[..MaxVolumeLabelLength];
        }
        _volumeLabel = label;

        IsMounted = true;
        MountStateChanged?.Invoke(this, true);

        _logger.LogInformation("VFS mounted: {ArchiveCount} archives discovered", archives.Count);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Hard stop: clears archive nodes without draining. In-flight operations may
    /// call Exit() on detached nodes (safe — operates on the object, not the dictionary).
    /// This is acceptable because StopAsync in DokanHostedService removes the Dokan mount
    /// point first, which stops new Dokan callbacks from arriving.
    /// </remarks>
    public Task UnmountAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Unmounting VFS");

        _structureCache.Clear();
        foreach (var node in _archiveNodes.Values)
            node.Dispose();
        _archiveNodes.Clear();
        Interlocked.Exchange(ref _totalArchiveBytes, 0);
        IsMounted = false;
        MountStateChanged?.Invoke(this, false);

        return Task.CompletedTask;
    }

    // ── IArchiveManager ─────────────────────────────────────────────────

    /// <inheritdoc />
    public Task AddArchiveAsync(ArchiveDescriptor archive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        _archiveTrie.AddArchive(archive);
        // Use GetOrAdd to preserve existing node if in-flight operations hold it.
        // Only RemoveArchiveAsync should replace/dispose nodes.
        _archiveNodes.GetOrAdd(archive.VirtualPath, _ => new ArchiveNode(archive));
        Interlocked.Add(ref _totalArchiveBytes, archive.SizeBytes);
        _logger.LogInformation("Archive added: {VirtualPath}", archive.VirtualPath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RemoveArchiveAsync(string archiveKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveKey);

        if (!_archiveNodes.TryGetValue(archiveKey, out ArchiveNode? node))
        {
            _logger.LogWarning("RemoveArchive: {Key} not found", archiveKey);
            return;
        }

        // 1. Drain in-flight operations for this archive
        _logger.LogInformation("Draining operations for archive: {Key}", archiveKey);
        await node.DrainAsync(DrainTimeout);
        if (node.ActiveOps > 0)
            _logger.LogWarning("Drain timeout: {Key} still has {Count} active ops", archiveKey, node.ActiveOps);

        // 2. Remove from trie (new lookups return NotFound)
        _archiveTrie.RemoveArchive(archiveKey);
        _archiveNodes.TryRemove(archiveKey, out _);
        Interlocked.Add(ref _totalArchiveBytes, -node.Descriptor.SizeBytes);
        node.Dispose(); // Release CancellationTokenSource

        // 3. Invalidate structure cache
        _structureCache.Invalidate(archiveKey);

        // 4. Remove file content cache entries
        _fileContentCache.RemoveArchive(archiveKey);

        _logger.LogInformation("Archive removed: {Key}", archiveKey);
    }

    /// <inheritdoc />
    public IEnumerable<ArchiveDescriptor> GetRegisteredArchives() =>
        _archiveNodes.Values.Select(n => n.Descriptor);

    /// <inheritdoc />
    public async Task<VfsFileInfo> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureMounted();

        ArchiveTrieResult result = _pathResolver.Resolve(path);

        switch (result.Status)
        {
            case ArchiveTrieStatus.VirtualRoot:
                return MakeDirectoryInfo("", "");

            case ArchiveTrieStatus.VirtualFolder:
                return MakeDirectoryInfo(
                    GetLastSegment(result.VirtualFolderPath!),
                    result.VirtualFolderPath!);

            case ArchiveTrieStatus.ArchiveRoot:
            {
                if (!ArchiveGuard.TryEnter(_archiveNodes, result.Archive!.VirtualPath, out var guard))
                    throw new VfsFileNotFoundException(path ?? "");
                using (guard)
                    return new VfsFileInfo
                    {
                        Name = result.Archive!.Name,
                        FullPath = result.Archive.VirtualPath,
                        IsDirectory = true,
                        SizeBytes = result.Archive.SizeBytes,
                        CreationTimeUtc = result.Archive.LastModifiedUtc,
                        LastWriteTimeUtc = result.Archive.LastModifiedUtc,
                        LastAccessTimeUtc = result.Archive.LastModifiedUtc,
                        Attributes = FileAttributes.Directory | FileAttributes.ReadOnly
                    };
            }

            case ArchiveTrieStatus.InsideArchive:
            {
                if (!ArchiveGuard.TryEnter(_archiveNodes, result.Archive!.VirtualPath, out var guard))
                    throw new VfsFileNotFoundException(path ?? "");
                using (guard)
                    return await GetArchiveEntryInfoAsync(
                        result.Archive!, result.InternalPath, cancellationToken).ConfigureAwait(false);
            }

            case ArchiveTrieStatus.NotFound:
                throw new VfsFileNotFoundException(path ?? "");

            default:
                throw new VfsException($"Unexpected resolution status: {result.Status}");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VfsFileInfo>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureMounted();

        ArchiveTrieResult result = _pathResolver.Resolve(path);

        IReadOnlyList<VfsFileInfo> listing;

        if (result.Status is ArchiveTrieStatus.ArchiveRoot or ArchiveTrieStatus.InsideArchive)
        {
            if (!ArchiveGuard.TryEnter(_archiveNodes, result.Archive!.VirtualPath, out var guard))
                throw new VfsDirectoryNotFoundException(path ?? "");

            using (guard)
            {
                string internalPath = result.Status == ArchiveTrieStatus.ArchiveRoot ? "" : result.InternalPath;
                listing = await ListArchiveDirectoryAsync(
                    result.Archive!, internalPath, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            listing = result.Status switch
            {
                ArchiveTrieStatus.VirtualRoot => ListVirtualFolder(""),
                ArchiveTrieStatus.VirtualFolder => ListVirtualFolder(result.VirtualFolderPath!),
                ArchiveTrieStatus.NotFound => throw new VfsDirectoryNotFoundException(path ?? ""),
                _ => throw new VfsException($"Unexpected resolution status: {result.Status}")
            };
        }

        // Trigger prefetch fire-and-forget after listing completes (FindFiles trigger)
        if (_prefetchOptions.Enabled && _prefetchOptions.OnListDirectory &&
            (result.Status == ArchiveTrieStatus.ArchiveRoot || result.Status == ArchiveTrieStatus.InsideArchive))
        {
            string internalDir = result.Status == ArchiveTrieStatus.ArchiveRoot ? "" : result.InternalPath;
            _ = PrefetchDirectoryAsync(result.Archive!, internalDir, triggerEntry: null);
        }

        return listing;
    }

    /// <inheritdoc />
    public async Task<int> ReadFileAsync(string path, byte[] buffer, long offset, CancellationToken cancellationToken = default)
    {
        EnsureMounted();

        ArchiveTrieResult result = _pathResolver.Resolve(path);

        if (result.Status != ArchiveTrieStatus.InsideArchive)
        {
            if (result.Status == ArchiveTrieStatus.NotFound)
                throw new VfsFileNotFoundException(path ?? "");
            throw new VfsAccessDeniedException(path ?? "");
        }

        ArchiveDescriptor archive = result.Archive!;
        string internalPath = result.InternalPath;

        // Per-archive guard: reject if archive is draining
        if (!ArchiveGuard.TryEnter(_archiveNodes, archive.VirtualPath, out var guard))
            throw new VfsFileNotFoundException(path ?? "");

        using (guard)
        {
            // Get archive structure
            ArchiveStructure structure = await _structureCache.GetOrBuildAsync(
                archive.VirtualPath, archive.PhysicalPath, cancellationToken).ConfigureAwait(false);

            ZipEntryInfo? entry = structure.GetEntry(internalPath);

            // Also check with trailing slash for directories
            if (entry == null)
            {
                string dirPath = internalPath.EndsWith('/') ? internalPath : internalPath + "/";
                entry = structure.GetEntry(dirPath);
            }

            if (entry == null)
                throw new VfsFileNotFoundException(path ?? "");

            if (entry.Value.IsDirectory)
                throw new VfsAccessDeniedException(path ?? "");

            // Delegate to file content cache — it owns extraction, caching, and byte retrieval
            string cacheKey = $"{archive.VirtualPath}:{internalPath}";
            bool wasCached = _fileContentCache.ContainsKey(cacheKey);
            if (!wasCached)
                _logger.LogInformation("Read (miss): {CacheKey} offset={Offset}", cacheKey, offset);
            else
                _logger.LogDebug("Read (hit): {CacheKey} offset={Offset}", cacheKey, offset);
            int bytesRead = await _fileContentCache.ReadAsync(
                archive.PhysicalPath, entry.Value, cacheKey, buffer, offset, cancellationToken).ConfigureAwait(false);

            // Trigger prefetch fire-and-forget only on a cold read
            if (!wasCached && _prefetchOptions.Enabled && _prefetchOptions.OnRead)
            {
                string dirPath = GetDirectoryPath(internalPath);
                _ = PrefetchDirectoryAsync(archive, dirPath, triggerEntry: entry.Value);
            }

            return bytesRead;
        }
    }

    /// <inheritdoc />
    public async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureMounted();

        ArchiveTrieResult result = _pathResolver.Resolve(path);

        if (result.Status != ArchiveTrieStatus.InsideArchive)
            return false;

        if (!ArchiveGuard.TryEnter(_archiveNodes, result.Archive!.VirtualPath, out var guard))
            return false; // Archive draining — treat as not found

        using (guard)
        {
            ArchiveStructure structure = await _structureCache.GetOrBuildAsync(
                result.Archive!.VirtualPath, result.Archive.PhysicalPath, cancellationToken).ConfigureAwait(false);

            ZipEntryInfo? entry = structure.GetEntry(result.InternalPath);
            return entry.HasValue && !entry.Value.IsDirectory;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureMounted();

        ArchiveTrieResult result = _pathResolver.Resolve(path);

        switch (result.Status)
        {
            case ArchiveTrieStatus.VirtualRoot:
            case ArchiveTrieStatus.VirtualFolder:
            case ArchiveTrieStatus.ArchiveRoot:
                return true;

            case ArchiveTrieStatus.InsideArchive:
            {
                if (!ArchiveGuard.TryEnter(_archiveNodes, result.Archive!.VirtualPath, out var guard))
                    return false;
                using (guard)
                    return await CheckArchiveDirectoryExistsAsync(
                        result.Archive!, result.InternalPath, cancellationToken).ConfigureAwait(false);
            }

            default:
                return false;
        }
    }

    /// <inheritdoc />
    public VfsVolumeInfo GetVolumeInfo()
    {
        long totalBytes = Interlocked.Read(ref _totalArchiveBytes);

        _logger.LogDebug("GetVolumeInfo: label={Label} totalBytes={TotalBytes:N0}",
            _volumeLabel, totalBytes);

        return new VfsVolumeInfo
        {
            VolumeLabel = _volumeLabel,
            FileSystemName = "NTFS",
            TotalBytes = totalBytes,
            FreeBytes = 0,
            IsReadOnly = true
        };
    }

    // ── Prefetch ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fire-and-forget prefetch for a directory. Uses a per-directory in-flight guard
    /// to prevent duplicate concurrent sequential reads of the same directory.
    /// </summary>
    private async Task PrefetchDirectoryAsync(
        ArchiveDescriptor archive,
        string dirInternalPath,
        ZipEntryInfo? triggerEntry)
    {
        // Participate in per-archive drain guard
        if (!ArchiveGuard.TryEnter(_archiveNodes, archive.VirtualPath, out var archiveGuard))
            return; // Archive draining — skip prefetch

        string guardKey = $"{archive.VirtualPath}:{dirInternalPath}";

        // In-flight guard: only the first concurrent caller proceeds
        if (!_prefetchInFlight.TryAdd(guardKey, 0))
        {
            archiveGuard.Dispose(); // Release archive guard
            PrefetchTelemetry.SkippedInFlight.Add(1);
            _logger.LogInformation("Prefetch skipped (already in-flight): {Archive}/{Dir}", archive.VirtualPath, dirInternalPath);
            return;
        }

        _logger.LogInformation("Prefetch triggered: {Archive}/{Dir}", archive.VirtualPath, dirInternalPath);

        // Combine application stopping with per-archive drain token
        CancellationToken appStopping = _appLifetime.ApplicationStopping;
        CancellationToken drainToken = _archiveNodes.TryGetValue(archive.VirtualPath, out var node)
            ? node.DrainToken
            : CancellationToken.None;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(appStopping, drainToken);
        CancellationToken ct = linkedCts.Token;

        try
        {
            await PrefetchSiblingsAsync(archive, dirInternalPath, triggerEntry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Application shutting down or archive draining — expected, swallow silently
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Prefetch failed for archive={Archive} dir={Dir}", archive.VirtualPath, dirInternalPath);
        }
        finally
        {
            _prefetchInFlight.TryRemove(guardKey, out _);
            archiveGuard.Dispose(); // Release per-archive drain guard
        }
    }

    /// <summary>
    /// Performs the actual sibling prefetch:
    /// 1. Lists directory and filters candidates by size threshold.
    /// 2. Applies MaxDirectoryFiles cap (nearest by offset to trigger).
    /// 3. Runs SpanSelector to pick the optimal contiguous window.
    /// 4. Opens one raw FileStream on the archive, seeks once to SpanStart,
    ///    and linearly reads: decompressing wanted entries, discarding holes.
    /// </summary>
    private async Task PrefetchSiblingsAsync(
        ArchiveDescriptor archive,
        string dirInternalPath,
        ZipEntryInfo? triggerEntry,
        CancellationToken ct)
    {
        ArchiveStructure structure = await _structureCache.GetOrBuildAsync(
            archive.VirtualPath, archive.PhysicalPath, ct).ConfigureAwait(false);

        // Build candidate list: non-directory files below size threshold
        long sizeThreshold = _prefetchOptions.FileSizeThresholdBytes;
        string dirPrefix = string.IsNullOrEmpty(dirInternalPath) ? "" : dirInternalPath + "/";
        List<(string InternalPath, ZipEntryInfo Entry)> allItems = structure.ListDirectory(dirInternalPath)
            .Where(item => !item.Entry.IsDirectory && item.Entry.UncompressedSize <= sizeThreshold)
            .Select(item => (InternalPath: dirPrefix + item.Name, item.Entry))
            .ToList();

        if (allItems.Count == 0)
            return;

        // Apply MaxDirectoryFiles cap: keep entries nearest to trigger by LocalHeaderOffset
        long pivotOffset = triggerEntry?.LocalHeaderOffset ?? allItems[0].Entry.LocalHeaderOffset;
        IEnumerable<(string InternalPath, ZipEntryInfo Entry)> capped = allItems.Count > _prefetchOptions.MaxDirectoryFiles
            ? allItems
                .OrderBy(x => Math.Abs(x.Entry.LocalHeaderOffset - pivotOffset))
                .Take(_prefetchOptions.MaxDirectoryFiles)
            : allItems;

        // Exclude trigger from span candidates (it's already being read)
        List<ZipEntryInfo> candidates = capped
            .Where(x => triggerEntry == null || x.Entry.LocalHeaderOffset != triggerEntry.Value.LocalHeaderOffset)
            .Select(x => x.Entry)
            .ToList();

        if (candidates.Count == 0)
            return;

        // Use trigger or first candidate as the centering anchor
        ZipEntryInfo anchor = triggerEntry ?? candidates[0];

        PrefetchPlan plan = SpanSelector.Select(
            candidates,
            anchor,
            _prefetchOptions.MaxFiles,
            _prefetchOptions.FillRatioThreshold);

        if (plan.IsEmpty)
            return;

        // Build lookup: LocalHeaderOffset → (internalPath, entry)
        Dictionary<long, (string InternalPath, ZipEntryInfo Entry)> wantedByOffset =
            allItems
                .Where(x => plan.Entries.Any(e => e.LocalHeaderOffset == x.Entry.LocalHeaderOffset))
                .ToDictionary(x => x.Entry.LocalHeaderOffset);

        // Skip entries already in cache — no need to re-read or re-decompress them.
        // If all are warm, bail out entirely before opening the file.
        wantedByOffset = wantedByOffset
            .Where(kv => !_fileContentCache.ContainsKey($"{archive.VirtualPath}:{kv.Value.InternalPath}"))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (wantedByOffset.Count == 0)
        {
            _logger.LogInformation(
                "Prefetch skipped (all {Count} entries already cached): {Archive}/{Dir}",
                plan.Entries.Count, archive.VirtualPath, dirInternalPath);
            return;
        }

        // Sort the plan entries by offset for linear traversal
        IOrderedEnumerable<ZipEntryInfo> ordered = plan.Entries.OrderBy(e => e.LocalHeaderOffset);

        long bytesRead = 0;
        int filesWarmed = 0;
        var sw = Stopwatch.StartNew();

        await using FileStream zipStream = new(
            archive.PhysicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            useAsync: true);

        // Seek once to span start
        zipStream.Seek(plan.SpanStart, SeekOrigin.Begin);
        long currentPos = plan.SpanStart;

        byte[] discardBuffer = ArrayPool<byte>.Shared.Rent(DiscardBufferSize);
        byte[] headerBuffer = new byte[LocalHeaderFixedSize];

        try
        {
            foreach (ZipEntryInfo wantedEntry in ordered)
            {
                ct.ThrowIfCancellationRequested();

                // Skip (discard) any bytes before this entry's local header
                long gap = wantedEntry.LocalHeaderOffset - currentPos;
                if (gap < 0)
                {
                    // Stream overshot (shouldn't happen with a valid plan) — skip this entry
                    continue;
                }

                if (gap > 0)
                {
                    long discarded = await DiscardBytesAsync(zipStream, gap, discardBuffer, ct).ConfigureAwait(false);
                    bytesRead += discarded;
                    currentPos += discarded;
                }

                // Parse the local header (30 bytes fixed)
                int headerRead = await ReadExactAsync(zipStream, headerBuffer, 0, LocalHeaderFixedSize, ct).ConfigureAwait(false);
                if (headerRead < LocalHeaderFixedSize)
                    break; // Truncated archive
                currentPos += headerRead;
                bytesRead += headerRead;

                // Read FileNameLength (offset 26) and ExtraFieldLength (offset 28) — little-endian
                ushort fileNameLen = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(FileNameLengthOffset, 2));
                ushort extraLen = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(28, 2));
                int variableHeaderLen = fileNameLen + extraLen;

                // Skip filename + extra field
                if (variableHeaderLen > 0)
                {
                    long skipped = await DiscardBytesAsync(zipStream, variableHeaderLen, discardBuffer, ct).ConfigureAwait(false);
                    currentPos += skipped;
                    bytesRead += skipped;
                }

                // Compressed data starts here — use CompressedSize from Central Directory (more reliable)
                long compressedSize = wantedEntry.CompressedSize;

                if (!wantedByOffset.TryGetValue(wantedEntry.LocalHeaderOffset, out var wanted))
                {
                    // Not a wanted entry (hole) — discard compressed bytes
                    long discarded = await DiscardBytesAsync(zipStream, compressedSize, discardBuffer, ct).ConfigureAwait(false);
                    currentPos += discarded;
                    bytesRead += discarded;
                    continue;
                }

                // Wanted entry — read compressed bytes and decompress
                string cacheKey = $"{archive.VirtualPath}:{wanted.InternalPath}";

                // Read compressed data into a pooled buffer
                byte[] compressedBuffer = ArrayPool<byte>.Shared.Rent((int)compressedSize);
                try
                {
                    int compRead = await ReadExactAsync(zipStream, compressedBuffer, 0, (int)compressedSize, ct).ConfigureAwait(false);
                    currentPos += compRead;
                    bytesRead += compRead;

                    // Decompress into MemoryStream
                    MemoryStream decompressed = new((int)wantedEntry.UncompressedSize);
                    ushort compressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(8, 2));

                    if (compressionMethod == 0) // Store
                    {
                        await decompressed.WriteAsync(compressedBuffer.AsMemory(0, compRead), ct).ConfigureAwait(false);
                    }
                    else if (compressionMethod == 8) // Deflate
                    {
                        await using var deflate = new DeflateStream(
                            new MemoryStream(compressedBuffer, 0, compRead, writable: false),
                            CompressionMode.Decompress,
                            leaveOpen: false);
                        await deflate.CopyToAsync(decompressed, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Unsupported compression — skip warming this entry
                        continue;
                    }

                    decompressed.Position = 0;
                    await _fileContentCache.WarmAsync(wantedEntry, cacheKey, decompressed, ct).ConfigureAwait(false);
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

        PrefetchTelemetry.FilesWarmed.Add(filesWarmed);
        PrefetchTelemetry.BytesRead.Add(bytesRead);
        PrefetchTelemetry.SpanReadDuration.Record(sw.Elapsed.TotalMilliseconds);

        _logger.LogInformation(
            "Prefetch complete: {Archive}/{Dir} — {Files}/{Candidates} files warmed, {Bytes:N0} bytes read in {Ms:F1} ms",
            archive.VirtualPath, dirInternalPath, filesWarmed, wantedByOffset.Count + filesWarmed, bytesRead, sw.Elapsed.TotalMilliseconds);
    }

    // ── Sequential I/O helpers ───────────────────────────────────────────────

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

    // === Private helpers ===

    private void EnsureMounted()
    {
        if (!IsMounted)
            throw new InvalidOperationException("VFS is not mounted. Call MountAsync first.");
    }

    private IReadOnlyList<VfsFileInfo> ListVirtualFolder(string folderPath)
    {
        return _archiveTrie.ListFolder(folderPath)
            .Select(entry => entry.IsArchive
                ? new VfsFileInfo
                {
                    Name = entry.Name,
                    FullPath = entry.Archive!.VirtualPath,
                    IsDirectory = true,
                    SizeBytes = entry.Archive.SizeBytes,
                    CreationTimeUtc = entry.Archive.LastModifiedUtc,
                    LastWriteTimeUtc = entry.Archive.LastModifiedUtc,
                    LastAccessTimeUtc = entry.Archive.LastModifiedUtc,
                    Attributes = FileAttributes.Directory | FileAttributes.ReadOnly
                }
                : MakeDirectoryInfo(entry.Name, string.IsNullOrEmpty(folderPath) ? entry.Name : $"{folderPath}/{entry.Name}"))
            .ToList();
    }

    private async Task<IReadOnlyList<VfsFileInfo>> ListArchiveDirectoryAsync(
        ArchiveDescriptor archive, string internalPath, CancellationToken cancellationToken)
    {
        ArchiveStructure structure = await _structureCache.GetOrBuildAsync(
            archive.VirtualPath, archive.PhysicalPath, cancellationToken).ConfigureAwait(false);

        string basePath = string.IsNullOrEmpty(internalPath)
            ? archive.VirtualPath
            : $"{archive.VirtualPath}/{internalPath}";

        return structure.ListDirectory(internalPath)
            .Select(item => new VfsFileInfo
            {
                Name = item.Name,
                FullPath = $"{basePath}/{item.Name}",
                IsDirectory = item.Entry.IsDirectory,
                SizeBytes = item.Entry.IsDirectory ? 0 : item.Entry.UncompressedSize,
                CreationTimeUtc = item.Entry.LastModified,
                LastWriteTimeUtc = item.Entry.LastModified,
                LastAccessTimeUtc = item.Entry.LastModified,
                Attributes = item.Entry.Attributes
            })
            .ToList();
    }

    private async Task<VfsFileInfo> GetArchiveEntryInfoAsync(
        ArchiveDescriptor archive, string internalPath, CancellationToken cancellationToken)
    {
        ArchiveStructure structure = await _structureCache.GetOrBuildAsync(
            archive.VirtualPath, archive.PhysicalPath, cancellationToken).ConfigureAwait(false);

        // Try as file first
        ZipEntryInfo? entry = structure.GetEntry(internalPath);

        // Try as directory (with trailing /)
        if (entry == null)
        {
            string dirPath = internalPath.EndsWith('/') ? internalPath : internalPath + "/";
            entry = structure.GetEntry(dirPath);
        }

        if (entry == null)
            throw new VfsFileNotFoundException($"{archive.VirtualPath}/{internalPath}");

        return new VfsFileInfo
        {
            Name = GetLastSegment(internalPath),
            FullPath = $"{archive.VirtualPath}/{internalPath}",
            IsDirectory = entry.Value.IsDirectory,
            SizeBytes = entry.Value.IsDirectory ? 0 : entry.Value.UncompressedSize,
            CreationTimeUtc = entry.Value.LastModified,
            LastWriteTimeUtc = entry.Value.LastModified,
            LastAccessTimeUtc = entry.Value.LastModified,
            Attributes = entry.Value.Attributes
        };
    }

    private async Task<bool> CheckArchiveDirectoryExistsAsync(
        ArchiveDescriptor archive, string internalPath, CancellationToken cancellationToken)
    {
        ArchiveStructure structure = await _structureCache.GetOrBuildAsync(
            archive.VirtualPath, archive.PhysicalPath, cancellationToken).ConfigureAwait(false);

        return structure.DirectoryExists(internalPath);
    }

    private static VfsFileInfo MakeDirectoryInfo(string name, string fullPath) => new()
    {
        Name = name,
        FullPath = fullPath,
        IsDirectory = true,
        SizeBytes = 0,
        CreationTimeUtc = DateTime.UtcNow,
        LastWriteTimeUtc = DateTime.UtcNow,
        LastAccessTimeUtc = DateTime.UtcNow,
        Attributes = FileAttributes.Directory | FileAttributes.ReadOnly
    };

    private static string GetLastSegment(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        string trimmed = path.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
    }

    private static string GetDirectoryPath(string internalPath)
    {
        string trimmed = internalPath.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash >= 0 ? trimmed[..lastSlash] : "";
    }
}
