using System.Buffers;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
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
        VfsVolumeInfo vol = _vfs.GetVolumeInfo();
        totalSize = (ulong)Math.Max(0, vol.TotalBytes);
        freeSize = 0;
        volumeLabel = vol.VolumeLabel;
        return NtStatus.Success;
    }

    public int GetFileSecurityByName(string fileName, out uint fileAttributes, ref byte[]? securityDescriptor)
    {
        fileAttributes = 0;
        securityDescriptor = null;

        if (ShouldShortCircuitShellMetadata(fileName))
            return NtStatus.ObjectNameNotFound;

        try
        {
            VfsFileInfo vfsInfo = _vfs.GetFileInfoAsync(fileName).GetAwaiter().GetResult();
            fileAttributes = ToWin32Attributes(vfsInfo);
            return NtStatus.Success;
        }
        catch (VfsFileNotFoundException) { return NtStatus.ObjectNameNotFound; }
        catch (VfsDirectoryNotFoundException) { return NtStatus.ObjectPathNotFound; }
        catch (VfsAccessDeniedException) { return NtStatus.AccessDenied; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFileSecurityByName error: {Path}", fileName);
            return NtStatus.UnexpectedIoError;
        }
    }

    public ValueTask<CreateResult> CreateFile(
        string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[]? securityDescriptor, ulong allocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        _logger.LogDebug("CreateFile denied: {Path} options=0x{Options:X} access=0x{Access:X}", fileName, createOptions, grantedAccess);
        return V(CreateResult.Error(NtStatus.AccessDenied));
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
            info.Context = vfsInfo;
            info.IsDirectory = vfsInfo.IsDirectory;
            return new CreateResult(NtStatus.Success, ConvertToFileInfo(vfsInfo));
        }
        catch (VfsFileNotFoundException) { return CreateResult.Error(NtStatus.ObjectNameNotFound); }
        catch (VfsDirectoryNotFoundException) { return CreateResult.Error(NtStatus.ObjectPathNotFound); }
        catch (VfsAccessDeniedException) { return CreateResult.Error(NtStatus.AccessDenied); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenFile error: {Path}", fileName);
            return CreateResult.Error(NtStatus.UnexpectedIoError);
        }
    }

    public async ValueTask<ReadResult> ReadFile(
        string fileName, Memory<byte> buffer, ulong offset,
        FileOperationInfo info, CancellationToken ct)
    {
        _logger.LogDebug("ReadFile: {Path} offset={Offset} length={Length}", fileName, offset, buffer.Length);
        long startTimestamp = Stopwatch.GetTimestamp();
        byte[] rentedArray = ArrayPool<byte>.Shared.Rent(buffer.Length);

        try
        {
            int read = await _vfs.ReadFileAsync(fileName, rentedArray, checked((long)offset), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                RecordReadDuration(startTimestamp, "eof");
                return ReadResult.EndOfFile();
            }

            int bytesRead = Math.Min(read, buffer.Length);
            rentedArray.AsSpan(0, bytesRead).CopyTo(buffer.Span);

            RecordReadDuration(startTimestamp, "success");
            return ReadResult.Success((uint)bytesRead);
        }
        catch (VfsFileNotFoundException) { return ReadResult.Error(NtStatus.ObjectNameNotFound); }
        catch (VfsAccessDeniedException) { return ReadResult.Error(NtStatus.AccessDenied); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return ReadResult.Error(NtStatus.Cancelled); }
        catch (Exception ex)
        {
            RecordReadDuration(startTimestamp, "error");
            _logger.LogError(ex, "ReadFile error: {Path}", fileName);
            return ReadResult.Error(NtStatus.UnexpectedIoError);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedArray);
        }
    }

    public ValueTask<WriteResult> WriteFile(
        string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
        bool writeToEndOfFile, bool constrainedIo,
        FileOperationInfo info, CancellationToken ct)
    {
        _logger.LogDebug("WriteFile denied: {Path}", fileName);
        return V(WriteResult.Error(NtStatus.AccessDenied));
    }

    public async ValueTask<FsResult> FlushFileBuffers(string? fileName, FileOperationInfo info, CancellationToken ct)
    {
        if (info.Context is VfsFileInfo vfsInfo)
            return FsResult.Success(ConvertToFileInfo(vfsInfo));

        if (!string.IsNullOrEmpty(fileName))
        {
            try
            {
                vfsInfo = await _vfs.GetFileInfoAsync(fileName, ct).ConfigureAwait(false);
                return FsResult.Success(ConvertToFileInfo(vfsInfo));
            }
            catch (VfsFileNotFoundException) { return FsResult.Error(NtStatus.ObjectNameNotFound); }
            catch (VfsDirectoryNotFoundException) { return FsResult.Error(NtStatus.ObjectPathNotFound); }
            catch (VfsAccessDeniedException) { return FsResult.Error(NtStatus.AccessDenied); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FlushFileBuffers error: {Path}", fileName);
                return FsResult.Error(NtStatus.UnexpectedIoError);
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
            VfsFileInfo vfsInfo = info.Context is VfsFileInfo cachedInfo
                ? cachedInfo
                : await _vfs.GetFileInfoAsync(fileName, ct).ConfigureAwait(false);
            return FsResult.Success(ConvertToFileInfo(vfsInfo));
        }
        catch (VfsFileNotFoundException) { return FsResult.Error(NtStatus.ObjectNameNotFound); }
        catch (VfsDirectoryNotFoundException) { return FsResult.Error(NtStatus.ObjectPathNotFound); }
        catch (VfsAccessDeniedException) { return FsResult.Error(NtStatus.AccessDenied); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFileInformation error: {Path}", fileName);
            return FsResult.Error(NtStatus.UnexpectedIoError);
        }
    }

    public ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
    {
        _logger.LogDebug("CanDelete denied: {Path}", fileName);
        return V(NtStatus.AccessDenied);
    }

    public ValueTask<ReadDirectoryResult> ReadDirectory(
        string fileName, string? pattern, string? marker,
        nint buffer, uint length,
        FileOperationInfo info, CancellationToken ct)
    {
        return ReadDirectoryCoreAsync(fileName, pattern, marker, buffer, length, ct);
    }

    private async ValueTask<ReadDirectoryResult> ReadDirectoryCoreAsync(
        string fileName, string? pattern, string? marker, nint buffer, uint length, CancellationToken ct)
    {
        _logger.LogDebug("ReadDirectory: {Path} pattern={Pattern} marker={Marker}", fileName, pattern, marker);

        try
        {
            IReadOnlyList<VfsFileInfo> entries = await _vfs.ListDirectoryAsync(fileName, ct).ConfigureAwait(false);
            return WriteDirectoryBuffer(PrepareDirectoryEntries(entries, pattern, marker), buffer, length);
        }
        catch (VfsDirectoryNotFoundException) { return ReadDirectoryResult.Error(NtStatus.ObjectPathNotFound); }
        catch (VfsFileNotFoundException) { return ReadDirectoryResult.Error(NtStatus.ObjectNameNotFound); }
        catch (VfsAccessDeniedException) { return ReadDirectoryResult.Error(NtStatus.AccessDenied); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadDirectory error: {Path}", fileName);
            return ReadDirectoryResult.Error(NtStatus.UnexpectedIoError);
        }
    }

    internal async Task<IReadOnlyList<VfsFileInfo>> GuardedPrepareDirectoryEntriesAsync(
        string path, string? pattern = null, string? marker = null, CancellationToken ct = default)
    {
        IReadOnlyList<VfsFileInfo> entries = await _vfs.ListDirectoryAsync(path, ct).ConfigureAwait(false);
        return PrepareDirectoryEntries(entries, pattern, marker).ToArray();
    }

    public int GetDirInfoByName(string dirName, string entryName, out FspDirInfo dirInfo, FileOperationInfo info)
    {
        dirInfo = default;

        try
        {
            IReadOnlyList<VfsFileInfo> entries = _vfs.ListDirectoryAsync(dirName).GetAwaiter().GetResult();
            foreach (VfsFileInfo entry in entries)
            {
                if (!string.Equals(entry.Name, entryName, StringComparison.OrdinalIgnoreCase))
                    continue;

                dirInfo = ToDirInfo(entry);
                return NtStatus.Success;
            }

            return NtStatus.ObjectNameNotFound;
        }
        catch (VfsDirectoryNotFoundException) { return NtStatus.ObjectPathNotFound; }
        catch (VfsFileNotFoundException) { return NtStatus.ObjectNameNotFound; }
        catch (VfsAccessDeniedException) { return NtStatus.AccessDenied; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDirInfoByName error: {Dir}/{Entry}", dirName, entryName);
            return NtStatus.UnexpectedIoError;
        }
    }

    private static unsafe ReadDirectoryResult WriteDirectoryBuffer(
        IEnumerable<VfsFileInfo> entries, nint buffer, uint length)
    {
        uint bytesTransferred = 0;

        foreach (VfsFileInfo entry in entries)
        {
            FspDirInfo dirInfo = ToDirInfo(entry);

            if (!WinFspFileSystem.AddDirInfo(&dirInfo, buffer, length, &bytesTransferred))
                return ReadDirectoryResult.Success(bytesTransferred);
        }

        WinFspFileSystem.EndDirInfo(buffer, length, &bytesTransferred);
        return ReadDirectoryResult.Success(bytesTransferred);
    }

    internal static IEnumerable<VfsFileInfo> PrepareDirectoryEntries(
        IEnumerable<VfsFileInfo> entries, string? pattern, string? marker)
    {
        IEnumerable<VfsFileInfo> query = entries;

        if (!string.IsNullOrWhiteSpace(pattern) && pattern != "*")
        {
            query = query.Where(e => FileSystemName.MatchesWin32Expression(pattern, e.Name, ignoreCase: true));
        }

        query = query.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(marker))
        {
            query = query.Where(e => string.Compare(e.Name, marker, StringComparison.OrdinalIgnoreCase) > 0);
        }

        return query;
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
        uint attributes = (uint)entry.Attributes | FileAttributeReadonly;
        if (entry.IsDirectory)
            attributes |= FileAttributeDirectory;
        else if ((attributes & FileAttributeDirectory) == 0)
            attributes |= FileAttributeNormal;
        return attributes;
    }

    private static ulong AlignAllocation(ulong size)
        => size == 0 ? 0 : ((size + 4095UL) / 4096UL) * 4096UL;

    private static ulong ToFileTime(DateTime dt)
        => (ulong)(dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime()).ToFileTimeUtc();

    private static void RecordReadDuration(long startTimestamp, string result)
    {
        WinFspTelemetry.ReadDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("result", result));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueTask<CreateResult> V(CreateResult r) => new(r);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueTask<WriteResult> V(WriteResult r) => new(r);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueTask<int> V(int r) => new(r);
}
