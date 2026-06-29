using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinFsp.Native;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Exceptions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Thin adapter translating WinFsp.Native IFileSystem calls to IVirtualFileSystem.
/// Read-only: all write operations return AccessDenied.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinFspFileSystemAdapter : IFileSystem
{
    private const uint FileAttributeReadonly = 0x00000001;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;

    // 1601-01-01 is the FILETIME epoch; DateTime.ToFileTimeUtc() throws for anything earlier.
    private static readonly DateTime FileTimeEpochUtc = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Kernel metadata cache window. A small positive value lets WinFsp serve repeated attribute/
    // info probes (Explorer refreshes) from its cache instead of re-issuing blocking VFS lookups.
    // Bounded so a dynamically added/removed archive becomes visible within ~1s.
    private const uint FileInfoTimeoutMs = 1000;

    private readonly IVirtualFileSystem _vfs;
    private readonly ILogger<WinFspFileSystemAdapter> _logger;
    private readonly bool _shortCircuitShellMetadata;

    public WinFspFileSystemAdapter(IVirtualFileSystem vfs, IOptions<MountSettings> mountSettings, ILogger<WinFspFileSystemAdapter> logger)
    {
        _vfs = vfs;
        _logger = logger;
        _shortCircuitShellMetadata = mountSettings.Value.ShortCircuitShellMetadata;
    }

    public bool SynchronousIo => false;

    public int Init(FileSystemHost host)
    {
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.ReadOnlyVolume = true;
        host.PersistentAcls = false;
        host.PostCleanupWhenModifiedOnly = true;
        host.PassQueryDirectoryPattern = true;
        host.FileInfoTimeout = FileInfoTimeoutMs;
        // Report as "NTFS" so Windows path resolution works for elevated processes.
        host.FileSystemName = "NTFS";
        return NtStatus.Success;
    }

    public int Mounted(FileSystemHost host)
    {
        _logger.LogInformation("Drive mounted at {MountPoint}", host.MountPoint);
        return NtStatus.Success;
    }

    public void Unmounted(FileSystemHost host)
    {
        _logger.LogInformation("Drive unmounted");
    }

    public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
    {
        _logger.LogDebug("GetVolumeInfo");
        totalSize = 0;
        freeSize = 0;
        volumeLabel = "";

        try
        {
            VfsVolumeInfo vol = _vfs.GetVolumeInfo();
            totalSize = (ulong)Math.Max(0, vol.TotalBytes);
            volumeLabel = vol.VolumeLabel;
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetVolumeInfo error");
            return NtStatus.UnexpectedIoError;
        }
    }

    public async ValueTask<SecurityByNameResult> GetFileSecurityByName(
        string fileName, bool getSecurityDescriptor, CancellationToken ct)
    {
        if (ShouldShortCircuitShellMetadata(fileName))
            return SecurityByNameResult.Error(NtStatus.ObjectNameNotFound);

        try
        {
            VfsFileInfo vfsInfo = await _vfs.GetFileInfoAsync(fileName, ct).ConfigureAwait(false);
            // SecurityDescriptor left null → WinFsp skips access checks (read-only volume).
            return SecurityByNameResult.Success(ToWin32Attributes(vfsInfo));
        }
        catch (Exception ex)
        {
            return SecurityByNameResult.Error(MapVfsException(ex, fileName));
        }
    }

    public ValueTask<CreateResult> CreateFile(
        string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[]? securityDescriptor, ulong allocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        _logger.LogDebug("CreateFile denied: {Path} options=0x{Options:X} access=0x{Access:X}", fileName, createOptions, grantedAccess);
        return new ValueTask<CreateResult>(CreateResult.Error(NtStatus.AccessDenied));
    }

    public async ValueTask<CreateResult> OpenFile(
        string fileName, uint createOptions, uint grantedAccess,
        FileOperationInfo info, CancellationToken ct)
    {
        if (ShouldShortCircuitShellMetadata(fileName))
            return CreateResult.Error(NtStatus.ObjectNameNotFound);

        _logger.LogDebug("OpenFile: {Path} options=0x{Options:X} access=0x{Access:X}", fileName, createOptions, grantedAccess);

        try
        {
            VfsFileInfo vfsInfo = await _vfs.GetFileInfoAsync(fileName, ct).ConfigureAwait(false);
            info.Context = new HandleContext(vfsInfo);
            info.IsDirectory = vfsInfo.IsDirectory;
            return new CreateResult(NtStatus.Success, ConvertToFileInfo(vfsInfo));
        }
        catch (Exception ex)
        {
            return CreateResult.Error(MapVfsException(ex, fileName));
        }
    }

    public async ValueTask<ReadResult> ReadFile(
        string fileName, Memory<byte> buffer, ulong offset,
        FileOperationInfo info, CancellationToken ct)
    {
        _logger.LogDebug("ReadFile: {Path} offset={Offset} length={Length}", fileName, offset, buffer.Length);
        long startTimestamp = Stopwatch.GetTimestamp();

        // A read at/after EOF (or a pathological offset that overflows a signed long) returns no data.
        if (offset > (ulong)long.MaxValue)
        {
            RecordReadDuration(startTimestamp, "eof");
            return ReadResult.EndOfFile();
        }

        try
        {
            // Zero-copy: the VFS writes decompressed bytes directly into WinFsp's kernel buffer.
            // The cache caps the read at buffer.Length, so `read` never exceeds it.
            int read = await _vfs.ReadFileAsync(fileName, buffer, (long)offset, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                RecordReadDuration(startTimestamp, "eof");
                return ReadResult.EndOfFile();
            }

            RecordReadDuration(startTimestamp, "success");
            return ReadResult.Success((uint)read);
        }
        catch (Exception ex)
        {
            int status = MapVfsException(ex, fileName);
            RecordReadDuration(startTimestamp, status == NtStatus.Cancelled ? "cancelled" : "error");
            return ReadResult.Error(status);
        }
    }

    public ValueTask<WriteResult> WriteFile(
        string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
        bool writeToEndOfFile, bool constrainedIo,
        FileOperationInfo info, CancellationToken ct)
    {
        _logger.LogDebug("WriteFile denied: {Path}", fileName);
        return new ValueTask<WriteResult>(WriteResult.Error(NtStatus.AccessDenied));
    }

    public async ValueTask<FsResult> FlushFileBuffers(string? fileName, FileOperationInfo info, CancellationToken ct)
    {
        if (info.Context is HandleContext ctx)
            return FsResult.Success(ConvertToFileInfo(ctx.Info));

        if (!string.IsNullOrEmpty(fileName))
        {
            try
            {
                VfsFileInfo vfsInfo = await _vfs.GetFileInfoAsync(fileName, ct).ConfigureAwait(false);
                return FsResult.Success(ConvertToFileInfo(vfsInfo));
            }
            catch (Exception ex)
            {
                return FsResult.Error(MapVfsException(ex, fileName));
            }
        }

        return FsResult.Success();
    }

    public async ValueTask<FsResult> GetFileInformation(
        string fileName, FileOperationInfo info, CancellationToken ct)
    {
        _logger.LogDebug("GetFileInformation: {Path}", fileName);

        try
        {
            VfsFileInfo vfsInfo = info.Context is HandleContext ctx
                ? ctx.Info
                : await _vfs.GetFileInfoAsync(fileName, ct).ConfigureAwait(false);
            return FsResult.Success(ConvertToFileInfo(vfsInfo));
        }
        catch (Exception ex)
        {
            return FsResult.Error(MapVfsException(ex, fileName));
        }
    }

    public ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
    {
        _logger.LogDebug("CanDelete denied: {Path}", fileName);
        return new ValueTask<int>(NtStatus.AccessDenied);
    }

    public async ValueTask<ReadDirectoryResult> ReadDirectory(
        string fileName, string? pattern, string? marker,
        nint buffer, uint length,
        FileOperationInfo info, CancellationToken ct)
    {
        _logger.LogDebug("ReadDirectory: {Path} pattern={Pattern} marker={Marker}", fileName, pattern, marker);

        try
        {
            // Build the sorted, pattern-filtered snapshot once per enumeration (marker == null) and
            // cache it on the handle. Continuation pages reuse it and resume via binary search, so a
            // paginated listing is O(N log N) total instead of re-listing + re-sorting on every page.
            var ctx = info.Context as HandleContext;
            VfsFileInfo[] listing;
            if (marker == null || ctx?.Listing is null)
            {
                IReadOnlyList<VfsFileInfo> entries = await _vfs.ListDirectoryAsync(fileName, ct).ConfigureAwait(false);
                listing = SortAndFilter(entries, pattern);
                if (ctx != null)
                    ctx.Listing = listing;
            }
            else
            {
                listing = ctx.Listing;
            }

            return WriteDirectoryBuffer(listing, ResolveResumeIndex(listing, marker), buffer, length);
        }
        catch (Exception ex)
        {
            return ReadDirectoryResult.Error(MapVfsException(ex, fileName));
        }
    }

    internal async Task<IReadOnlyList<VfsFileInfo>> GuardedPrepareDirectoryEntriesAsync(
        string path, string? pattern = null, string? marker = null, CancellationToken ct = default)
    {
        IReadOnlyList<VfsFileInfo> entries = await _vfs.ListDirectoryAsync(path, ct).ConfigureAwait(false);
        VfsFileInfo[] sorted = SortAndFilter(entries, pattern);
        return sorted[ResolveResumeIndex(sorted, marker)..];
    }

    public async ValueTask<DirInfoByNameResult> GetDirInfoByName(
        string dirName, string entryName, FileOperationInfo info, CancellationToken ct)
    {
        string fullPath = dirName.Length == 0 || dirName[^1] is '\\' or '/'
            ? dirName + entryName
            : dirName + "\\" + entryName;

        if (ShouldShortCircuitShellMetadata(fullPath))
            return DirInfoByNameResult.Error(NtStatus.ObjectNameNotFound);

        try
        {
            // O(1) single-entry lookup via the VFS dictionary — avoids listing the entire parent.
            VfsFileInfo entry = await _vfs.GetFileInfoAsync(fullPath, ct).ConfigureAwait(false);
            return DirInfoByNameResult.Success(ToDirInfo(entry));
        }
        catch (Exception ex)
        {
            return DirInfoByNameResult.Error(MapVfsException(ex, $"{dirName}/{entryName}"));
        }
    }

    private static unsafe ReadDirectoryResult WriteDirectoryBuffer(
        VfsFileInfo[] entries, int start, nint buffer, uint length)
    {
        uint bytesTransferred = 0;

        for (int i = start; i < entries.Length; i++)
        {
            FspDirInfo dirInfo = ToDirInfo(entries[i]);

            if (!WinFspFileSystem.AddDirInfo(&dirInfo, buffer, length, &bytesTransferred))
                return ReadDirectoryResult.Success(bytesTransferred); // buffer full; WinFsp re-calls with marker
        }

        WinFspFileSystem.EndDirInfo(buffer, length, &bytesTransferred);
        return ReadDirectoryResult.Success(bytesTransferred);
    }

    /// <summary>
    /// Sorts directory entries by name (OrdinalIgnoreCase) after applying the Win32 wildcard
    /// pattern. The stable, deterministic ordering lets paginated reads resume by marker.
    /// </summary>
    internal static VfsFileInfo[] SortAndFilter(IEnumerable<VfsFileInfo> entries, string? pattern)
    {
        IEnumerable<VfsFileInfo> query = entries;

        if (!string.IsNullOrWhiteSpace(pattern) && pattern != "*")
            query = query.Where(e => FileSystemName.MatchesWin32Expression(pattern, e.Name, ignoreCase: true));

        return query.OrderBy(e => e.Name, EntryNameComparer).ToArray();
    }

    /// <summary>
    /// Returns the index of the first entry whose name sorts strictly after <paramref name="marker"/>.
    /// Binary search over the sorted snapshot — O(log N) per page.
    /// </summary>
    internal static int ResolveResumeIndex(VfsFileInfo[] sorted, string? marker)
    {
        if (string.IsNullOrEmpty(marker))
            return 0;

        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (CompareEntryNames(sorted[mid].Name, marker) <= 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static readonly IComparer<string> EntryNameComparer = Comparer<string>.Create(CompareEntryNames);

    /// <summary>
    /// Total ordering of entry names: case-insensitive primary (matching the volume's
    /// case-insensitivity) with an ordinal tiebreaker. The tiebreaker keeps entries that differ
    /// only by case distinct, so none is dropped or duplicated across a paginated marker resume.
    /// </summary>
    private static int CompareEntryNames(string a, string b)
    {
        int c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    public void Cleanup(string? fileName, FileOperationInfo info, CleanupFlags flags)
    {
        if (fileName != null)
            _logger.LogDebug("Cleanup: {Path} flags=0x{Flags:X}", fileName, (uint)flags);
    }

    public void Close(FileOperationInfo info)
    {
        info.Context = null;
    }

    public int GetFileSecurity(string fileName, ref byte[]? securityDescriptor, FileOperationInfo info)
    {
        securityDescriptor = null;
        return NtStatus.Success;
    }

    public int SetFileSecurity(string fileName, uint securityInformation, byte[] modificationDescriptor, FileOperationInfo info)
        => NtStatus.AccessDenied;

    // === Guarded async methods (test-facing API — no WinFsp native types) ===

    public Task<int> GuardedReadFileAsync(string path, byte[] buffer, long offset, CancellationToken ct = default)
        => _vfs.ReadFileAsync(path, buffer, offset, ct);

    public Task<IReadOnlyList<VfsFileInfo>> GuardedListDirectoryAsync(string path, CancellationToken ct = default)
        => _vfs.ListDirectoryAsync(path, ct);

    public Task<VfsFileInfo> GuardedGetFileInfoAsync(string path, CancellationToken ct = default)
        => _vfs.GetFileInfoAsync(path, ct);

    public Task<bool> GuardedFileExistsAsync(string path, CancellationToken ct = default)
        => _vfs.FileExistsAsync(path, ct);

    public Task<bool> GuardedDirectoryExistsAsync(string path, CancellationToken ct = default)
        => _vfs.DirectoryExistsAsync(path, ct);

    /// <summary>
    /// Maps a VFS exception to the corresponding NTSTATUS. Single source of truth shared by every
    /// callback so the mapping cannot drift between handlers.
    /// </summary>
    private int MapVfsException(Exception ex, string? path)
    {
        switch (ex)
        {
            case VfsFileNotFoundException: return NtStatus.ObjectNameNotFound;
            case VfsDirectoryNotFoundException: return NtStatus.ObjectPathNotFound;
            case VfsAccessDeniedException: return NtStatus.AccessDenied;
            case OperationCanceledException: return NtStatus.Cancelled;
            default:
                _logger.LogError(ex, "VFS operation failed: {Path}", path);
                return NtStatus.UnexpectedIoError;
        }
    }

    private bool ShouldShortCircuitShellMetadata(string path)
    {
        if (!_shortCircuitShellMetadata)
            return false;

        if (!ShellMetadataFilter.IsShellMetadataPath(path.AsSpan()))
            return false;

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Short-circuit shell metadata: {Path}", path);
        return true;
    }

    private static FspDirInfo ToDirInfo(VfsFileInfo entry)
    {
        var dirInfo = new FspDirInfo
        {
            FileInfo = ConvertToFileInfo(entry)
        };
        dirInfo.SetFileName(entry.Name);
        return dirInfo;
    }

    private static FspFileInfo ConvertToFileInfo(VfsFileInfo entry) => new()
    {
        FileAttributes = ToWin32Attributes(entry),
        FileSize = entry.IsDirectory ? 0UL : (ulong)Math.Max(0, entry.SizeBytes),
        AllocationSize = entry.IsDirectory ? 0UL : AlignAllocation((ulong)Math.Max(0, entry.SizeBytes)),
        CreationTime = ToFileTime(entry.CreationTimeUtc),
        LastAccessTime = ToFileTime(entry.LastAccessTimeUtc),
        LastWriteTime = ToFileTime(entry.LastWriteTimeUtc),
        ChangeTime = ToFileTime(entry.LastWriteTimeUtc),
    };

    private static uint ToWin32Attributes(VfsFileInfo entry)
    {
        // ReadOnly is always set (read-only volume). FILE_ATTRIBUTE_NORMAL is only valid in
        // isolation, so strip it — with ReadOnly always present it would otherwise form the invalid
        // Normal|<other> combination (archive entries are commonly seeded with Normal).
        uint attributes = ((uint)entry.Attributes | FileAttributeReadonly) & ~FileAttributeNormal;
        if (entry.IsDirectory)
            attributes |= FileAttributeDirectory;
        return attributes;
    }

    private static ulong AlignAllocation(ulong size)
        => size == 0 ? 0 : ((size + 4095UL) / 4096UL) * 4096UL;

    private static ulong ToFileTime(DateTime dt)
    {
        DateTime utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        // Clamp pre-epoch timestamps (e.g. DateTime.MinValue from archive entries lacking a
        // timestamp) to FILETIME 0; ToFileTimeUtc() would otherwise throw and fail the operation.
        return utc < FileTimeEpochUtc ? 0UL : (ulong)utc.ToFileTimeUtc();
    }

    private static void RecordReadDuration(long startTimestamp, string result)
    {
        WinFspTelemetry.ReadDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("result", result));
    }

    /// <summary>
    /// Per-handle state cached in <see cref="FileOperationInfo.Context"/>: the opened entry's
    /// metadata plus, for directory handles, the sorted enumeration snapshot reused across
    /// paginated <see cref="ReadDirectory"/> calls.
    /// </summary>
    private sealed class HandleContext(VfsFileInfo info)
    {
        public VfsFileInfo Info { get; } = info;
        public VfsFileInfo[]? Listing { get; set; }
    }
}
