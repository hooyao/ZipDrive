using Microsoft.Extensions.Logging;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Exceptions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Application.Services;

/// <summary>
/// Platform-independent virtual file system that mounts ZIP archives as folders.
/// Orchestrates archive trie, structure cache, file content cache, and ZIP reader.
/// </summary>
public sealed class ZipVirtualFileSystem : IVirtualFileSystem
{
    private readonly IArchiveTrie _archiveTrie;
    private readonly IArchiveStructureCache _structureCache;
    private readonly IFileContentCache _fileContentCache;
    private readonly IArchiveDiscovery _discovery;
    private readonly IPathResolver _pathResolver;
    private readonly ILogger<ZipVirtualFileSystem> _logger;

    public ZipVirtualFileSystem(
        IArchiveTrie archiveTrie,
        IArchiveStructureCache structureCache,
        IFileContentCache fileContentCache,
        IArchiveDiscovery discovery,
        IPathResolver pathResolver,
        ILogger<ZipVirtualFileSystem> logger)
    {
        _archiveTrie = archiveTrie;
        _structureCache = structureCache;
        _fileContentCache = fileContentCache;
        _discovery = discovery;
        _pathResolver = pathResolver;
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
            _archiveTrie.AddArchive(archive);
        }

        IsMounted = true;
        MountStateChanged?.Invoke(this, true);

        _logger.LogInformation("VFS mounted: {ArchiveCount} archives discovered", archives.Count);
    }

    /// <inheritdoc />
    public Task UnmountAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Unmounting VFS");

        _structureCache.Clear();
        IsMounted = false;
        MountStateChanged?.Invoke(this, false);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<VfsFileInfo> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureMounted();

        ArchiveTrieResult result = _pathResolver.Resolve(path);

        return result.Status switch
        {
            ArchiveTrieStatus.VirtualRoot => MakeDirectoryInfo("", ""),

            ArchiveTrieStatus.VirtualFolder => MakeDirectoryInfo(
                GetLastSegment(result.VirtualFolderPath!),
                result.VirtualFolderPath!),

            ArchiveTrieStatus.ArchiveRoot => new VfsFileInfo
            {
                Name = result.Archive!.Name,
                FullPath = result.Archive.VirtualPath,
                IsDirectory = true,
                SizeBytes = result.Archive.SizeBytes,
                CreationTimeUtc = result.Archive.LastModifiedUtc,
                LastWriteTimeUtc = result.Archive.LastModifiedUtc,
                LastAccessTimeUtc = result.Archive.LastModifiedUtc,
                Attributes = FileAttributes.Directory | FileAttributes.ReadOnly
            },

            ArchiveTrieStatus.InsideArchive => await GetArchiveEntryInfoAsync(
                result.Archive!, result.InternalPath, cancellationToken).ConfigureAwait(false),

            ArchiveTrieStatus.NotFound => throw new VfsFileNotFoundException(path ?? ""),

            _ => throw new VfsException($"Unexpected resolution status: {result.Status}")
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VfsFileInfo>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureMounted();

        ArchiveTrieResult result = _pathResolver.Resolve(path);

        return result.Status switch
        {
            ArchiveTrieStatus.VirtualRoot => ListVirtualFolder(""),
            ArchiveTrieStatus.VirtualFolder => ListVirtualFolder(result.VirtualFolderPath!),
            ArchiveTrieStatus.ArchiveRoot => await ListArchiveDirectoryAsync(
                result.Archive!, "", cancellationToken).ConfigureAwait(false),
            ArchiveTrieStatus.InsideArchive => await ListArchiveDirectoryAsync(
                result.Archive!, result.InternalPath, cancellationToken).ConfigureAwait(false),
            ArchiveTrieStatus.NotFound => throw new VfsDirectoryNotFoundException(path ?? ""),
            _ => throw new VfsException($"Unexpected resolution status: {result.Status}")
        };
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
        return await _fileContentCache.ReadAsync(
            archive.PhysicalPath, entry.Value, cacheKey, buffer, offset, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureMounted();

        ArchiveTrieResult result = _pathResolver.Resolve(path);

        if (result.Status != ArchiveTrieStatus.InsideArchive)
            return false;

        ArchiveStructure structure = await _structureCache.GetOrBuildAsync(
            result.Archive!.VirtualPath, result.Archive.PhysicalPath, cancellationToken).ConfigureAwait(false);

        ZipEntryInfo? entry = structure.GetEntry(result.InternalPath);
        return entry.HasValue && !entry.Value.IsDirectory;
    }

    /// <inheritdoc />
    public async Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureMounted();

        ArchiveTrieResult result = _pathResolver.Resolve(path);

        return result.Status switch
        {
            ArchiveTrieStatus.VirtualRoot => true,
            ArchiveTrieStatus.VirtualFolder => true,
            ArchiveTrieStatus.ArchiveRoot => true,
            ArchiveTrieStatus.InsideArchive => await CheckArchiveDirectoryExistsAsync(
                result.Archive!, result.InternalPath, cancellationToken).ConfigureAwait(false),
            _ => false
        };
    }

    /// <inheritdoc />
    public VfsVolumeInfo GetVolumeInfo()
    {
        return new VfsVolumeInfo
        {
            VolumeLabel = "ZipDrive",
            FileSystemName = "ZipDriveFS",
            TotalBytes = 0,
            FreeBytes = 0,
            IsReadOnly = true
        };
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
}
