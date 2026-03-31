using System.Collections.Concurrent;
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
public sealed class ArchiveVirtualFileSystem : IVirtualFileSystem, IArchiveManager
{
    private readonly IArchiveTrie _archiveTrie;
    private readonly IArchiveStructureCache _structureCache;
    private readonly IFileContentCache _fileContentCache;
    private readonly IArchiveDiscovery _discovery;
    private readonly IPathResolver _pathResolver;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly IFormatRegistry _formatRegistry;
    private readonly MountSettings _mountSettings;
    private readonly PrefetchOptions _prefetchOptions;
    private readonly ILogger<ArchiveVirtualFileSystem> _logger;
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


    public ArchiveVirtualFileSystem(
        IArchiveTrie archiveTrie,
        IArchiveStructureCache structureCache,
        IFileContentCache fileContentCache,
        IArchiveDiscovery discovery,
        IPathResolver pathResolver,
        IHostApplicationLifetime appLifetime,
        IFormatRegistry formatRegistry,
        IOptions<MountSettings> mountSettings,
        IOptions<PrefetchOptions> prefetchOptions,
        ILogger<ArchiveVirtualFileSystem> logger)
    {
        _archiveTrie = archiveTrie;
        _structureCache = structureCache;
        _fileContentCache = fileContentCache;
        _discovery = discovery;
        _pathResolver = pathResolver;
        _appLifetime = appLifetime;
        _formatRegistry = formatRegistry;
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
    public async Task AddArchiveAsync(ArchiveDescriptor archive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archive);

        // Probe for unsupported archive variants (e.g., solid RAR) before trie registration
        IArchiveStructureBuilder builder = _formatRegistry.GetStructureBuilder(archive.FormatId);
        ArchiveProbeResult probe = await builder.ProbeAsync(archive.PhysicalPath, ct).ConfigureAwait(false);

        if (!probe.IsSupported)
        {
            if (_mountSettings.HideUnsupportedArchives)
            {
                _logger.LogWarning(
                    "{Archive} filtered (unsupported: {Reason}). Set Mount:HideUnsupportedArchives=false to show.",
                    archive.VirtualPath, probe.UnsupportedReason);
                return;
            }

            // Register with (NOT SUPPORTED) suffix — user sees it in Explorer
            string suffixedPath = archive.VirtualPath + ArchiveProbeResult.UnsupportedFolderSuffix;
            _logger.LogWarning(
                "Unsupported archive: {Archive} ({Reason}). Showing as \"{SuffixedPath}\"",
                archive.VirtualPath, probe.UnsupportedReason, suffixedPath);

            var suffixedDescriptor = new ArchiveDescriptor
            {
                VirtualPath = suffixedPath,
                PhysicalPath = archive.PhysicalPath,
                SizeBytes = archive.SizeBytes,
                LastModifiedUtc = archive.LastModifiedUtc,
                FormatId = archive.FormatId
            };
            _archiveTrie.AddArchive(suffixedDescriptor);
            _archiveNodes.TryAdd(suffixedDescriptor.VirtualPath, new ArchiveNode(suffixedDescriptor));
            return;
        }

        _archiveTrie.AddArchive(archive);
        bool added = _archiveNodes.TryAdd(archive.VirtualPath, new ArchiveNode(archive));
        if (added)
            Interlocked.Add(ref _totalArchiveBytes, archive.SizeBytes);
        _logger.LogInformation("Archive added: {VirtualPath} (format: {Format})", archive.VirtualPath, archive.FormatId);
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

        // 5. Notify format providers of archive removal (metadata cleanup)
        _formatRegistry.OnArchiveRemoved(archiveKey);

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
            _ = PrefetchDirectoryAsync(result.Archive!, internalDir, triggerEntry: null, triggerInternalPath: null);
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
                archive.VirtualPath, archive.PhysicalPath, archive.FormatId, cancellationToken).ConfigureAwait(false);

            ArchiveEntryInfo? entry = structure.GetEntry(internalPath);

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
                archive.PhysicalPath, archive.FormatId, entry.Value, internalPath, cacheKey, buffer, offset, cancellationToken).ConfigureAwait(false);

            // Trigger prefetch fire-and-forget only on a cold read
            if (!wasCached && _prefetchOptions.Enabled && _prefetchOptions.OnRead)
            {
                string dirPath = GetDirectoryPath(internalPath);
                _ = PrefetchDirectoryAsync(archive, dirPath, triggerEntry: entry.Value, triggerInternalPath: internalPath);
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
                result.Archive!.VirtualPath, result.Archive.PhysicalPath, result.Archive.FormatId, cancellationToken).ConfigureAwait(false);

            ArchiveEntryInfo? entry = structure.GetEntry(result.InternalPath);
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
        ArchiveEntryInfo? triggerEntry,
        string? triggerInternalPath)
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
            IPrefetchStrategy? prefetchStrategy = _formatRegistry.GetPrefetchStrategy(archive.FormatId);
            if (prefetchStrategy != null)
            {
                ArchiveStructure structure = await _structureCache.GetOrBuildAsync(
                    archive.VirtualPath, archive.PhysicalPath, archive.FormatId, ct).ConfigureAwait(false);
                await prefetchStrategy.PrefetchAsync(
                    archive.PhysicalPath, structure, dirInternalPath, triggerEntry,
                    triggerInternalPath, _fileContentCache, _prefetchOptions, ct).ConfigureAwait(false);
            }
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
            archive.VirtualPath, archive.PhysicalPath, archive.FormatId, cancellationToken).ConfigureAwait(false);

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
            archive.VirtualPath, archive.PhysicalPath, archive.FormatId, cancellationToken).ConfigureAwait(false);

        // Try as file first
        ArchiveEntryInfo? entry = structure.GetEntry(internalPath);

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
            archive.VirtualPath, archive.PhysicalPath, archive.FormatId, cancellationToken).ConfigureAwait(false);

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
