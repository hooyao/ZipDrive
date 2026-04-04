using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Fsp;
using Fsp.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Exceptions;
using ZipDrive.Domain.Models;
using FileInfo = Fsp.Interop.FileInfo;

namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Thin adapter translating WinFsp FileSystemBase calls to IVirtualFileSystem.
/// Read-only: all write operations return STATUS_ACCESS_DENIED.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinFspFileSystemAdapter : FileSystemBase
{
    private readonly IVirtualFileSystem _vfs;
    private readonly ILogger<WinFspFileSystemAdapter> _logger;
    private readonly bool _shortCircuitShellMetadata;

    public WinFspFileSystemAdapter(
        IVirtualFileSystem vfs,
        IOptions<MountSettings> mountSettings,
        ILogger<WinFspFileSystemAdapter> logger)
    {
        _vfs = vfs;
        _logger = logger;
        _shortCircuitShellMetadata = mountSettings.Value.ShortCircuitShellMetadata;
    }

    // === File Open / Close ===

    public override Int32 GetSecurityByName(
        String FileName,
        out UInt32 FileAttributes,
        ref Byte[] SecurityDescriptor)
    {
        // Short-circuit Windows shell metadata probes
        if (_shortCircuitShellMetadata && ShellMetadataFilter.IsShellMetadataPath(FileName.AsSpan()))
        {
            FileAttributes = default;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        try
        {
            bool isDir = _vfs.DirectoryExistsAsync(FileName).GetAwaiter().GetResult();
            if (isDir)
            {
                FileAttributes = (UInt32)System.IO.FileAttributes.Directory
                               | (UInt32)System.IO.FileAttributes.ReadOnly;
                return STATUS_SUCCESS;
            }

            bool isFile = _vfs.FileExistsAsync(FileName).GetAwaiter().GetResult();
            if (isFile)
            {
                FileAttributes = (UInt32)System.IO.FileAttributes.ReadOnly;
                return STATUS_SUCCESS;
            }

            FileAttributes = default;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        catch (VfsFileNotFoundException)
        {
            FileAttributes = default;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        catch (VfsDirectoryNotFoundException)
        {
            FileAttributes = default;
            return STATUS_OBJECT_PATH_NOT_FOUND;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSecurityByName error: {Path}", FileName);
            FileAttributes = default;
            return STATUS_UNEXPECTED_IO_ERROR;
        }
    }

    public override Int32 Open(
        String FileName,
        UInt32 CreateOptions,
        UInt32 GrantedAccess,
        out Object FileNode,
        out Object FileDesc,
        out FileInfo FileInfo,
        out String NormalizedName)
    {
        FileNode = default!;
        FileDesc = default!;
        FileInfo = default;
        NormalizedName = default!;

        // Short-circuit Windows shell metadata probes
        if (_shortCircuitShellMetadata && ShellMetadataFilter.IsShellMetadataPath(FileName.AsSpan()))
        {
            _logger.LogDebug("Open: short-circuit shell metadata: {Path}", FileName);
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        _logger.LogDebug("Open: {Path}", FileName);

        try
        {
            VfsFileInfo vfsInfo = _vfs.GetFileInfoAsync(FileName).GetAwaiter().GetResult();

            FileNode = new FileContext
            {
                Path = FileName,
                IsDirectory = vfsInfo.IsDirectory
            };
            FileDesc = null!;
            FileInfo = ToWinFspFileInfo(vfsInfo);
            NormalizedName = null!;

            return STATUS_SUCCESS;
        }
        catch (VfsFileNotFoundException)
        {
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        catch (VfsDirectoryNotFoundException)
        {
            return STATUS_OBJECT_PATH_NOT_FOUND;
        }
        catch (VfsAccessDeniedException)
        {
            return STATUS_ACCESS_DENIED;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open error: {Path}", FileName);
            return STATUS_UNEXPECTED_IO_ERROR;
        }
    }

    public override void Close(Object FileNode, Object FileDesc)
    {
        if (FileNode is FileContext ctx)
            _logger.LogDebug("Close: {Path}", ctx.Path);
    }

    // === Read ===

    public override Int32 Read(
        Object FileNode,
        Object FileDesc,
        IntPtr Buffer,
        UInt64 Offset,
        UInt32 Length,
        out UInt32 BytesTransferred)
    {
        BytesTransferred = 0;

        if (FileNode is not FileContext ctx)
            return STATUS_INVALID_PARAMETER;

        _logger.LogDebug("Read: {Path} offset={Offset} length={Length}", ctx.Path, Offset, Length);
        long startTimestamp = Stopwatch.GetTimestamp();

        byte[] rentedArray = ArrayPool<byte>.Shared.Rent((int)Length);
        try
        {
            int read = _vfs.ReadFileAsync(ctx.Path, rentedArray, (long)Offset)
                .GetAwaiter().GetResult();
            int bytesRead = Math.Min(read, (int)Length);

            if (bytesRead > 0)
                Marshal.Copy(rentedArray, 0, Buffer, bytesRead);

            BytesTransferred = (uint)bytesRead;

            FileSystemTelemetry.ReadDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                new KeyValuePair<string, object?>("result", "success"));

            return STATUS_SUCCESS;
        }
        catch (VfsFileNotFoundException)
        {
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        catch (VfsAccessDeniedException)
        {
            return STATUS_ACCESS_DENIED;
        }
        catch (Exception ex)
        {
            FileSystemTelemetry.ReadDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                new KeyValuePair<string, object?>("result", "error"));

            _logger.LogError(ex, "Read error: {Path}", ctx.Path);
            return STATUS_UNEXPECTED_IO_ERROR;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedArray);
        }
    }

    // === Directory Listing ===

    public override Boolean ReadDirectoryEntry(
        Object FileNode,
        Object FileDesc,
        String Pattern,
        String Marker,
        ref Object Context,
        out String FileName,
        out FileInfo FileInfo)
    {
        if (FileNode is not FileContext ctx)
        {
            FileName = default!;
            FileInfo = default;
            return false;
        }

        // Lazily populate directory entries on first call (Context == null)
        if (Context is not IEnumerator<VfsFileInfo> enumerator)
        {
            try
            {
                IReadOnlyList<VfsFileInfo> entries = _vfs.ListDirectoryAsync(ctx.Path)
                    .GetAwaiter().GetResult();

                // Add "." and ".." entries
                var allEntries = new List<VfsFileInfo>(entries.Count + 2);
                allEntries.Add(new VfsFileInfo
                {
                    Name = ".",
                    FullPath = ctx.Path,
                    IsDirectory = true,
                    SizeBytes = 0,
                    Attributes = System.IO.FileAttributes.Directory | System.IO.FileAttributes.ReadOnly,
                    CreationTimeUtc = DateTime.UtcNow,
                    LastWriteTimeUtc = DateTime.UtcNow,
                    LastAccessTimeUtc = DateTime.UtcNow
                });
                allEntries.Add(new VfsFileInfo
                {
                    Name = "..",
                    FullPath = ctx.Path,
                    IsDirectory = true,
                    SizeBytes = 0,
                    Attributes = System.IO.FileAttributes.Directory | System.IO.FileAttributes.ReadOnly,
                    CreationTimeUtc = DateTime.UtcNow,
                    LastWriteTimeUtc = DateTime.UtcNow,
                    LastAccessTimeUtc = DateTime.UtcNow
                });
                allEntries.AddRange(entries);

                enumerator = allEntries.GetEnumerator();
                Context = enumerator;

                // Skip to marker if provided (WinFsp uses markers for pagination)
                if (Marker is not null)
                {
                    while (enumerator.MoveNext())
                    {
                        if (string.Equals(enumerator.Current.Name, Marker, StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReadDirectoryEntry error: {Path}", ctx.Path);
                FileName = default!;
                FileInfo = default;
                return false;
            }
        }

        if (enumerator.MoveNext())
        {
            VfsFileInfo entry = enumerator.Current;
            FileName = entry.Name;
            FileInfo = ToWinFspFileInfo(entry);
            return true;
        }

        FileName = default!;
        FileInfo = default;
        return false;
    }

    // === File Information ===

    public override Int32 GetFileInfo(
        Object FileNode,
        Object FileDesc,
        out FileInfo FileInfo)
    {
        FileInfo = default;

        if (FileNode is not FileContext ctx)
            return STATUS_INVALID_PARAMETER;

        _logger.LogDebug("GetFileInfo: {Path}", ctx.Path);

        try
        {
            VfsFileInfo vfsInfo = _vfs.GetFileInfoAsync(ctx.Path).GetAwaiter().GetResult();
            FileInfo = ToWinFspFileInfo(vfsInfo);
            return STATUS_SUCCESS;
        }
        catch (VfsFileNotFoundException)
        {
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        catch (VfsDirectoryNotFoundException)
        {
            return STATUS_OBJECT_PATH_NOT_FOUND;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFileInfo error: {Path}", ctx.Path);
            return STATUS_UNEXPECTED_IO_ERROR;
        }
    }

    // === Volume Information ===

    public override Int32 GetVolumeInfo(out VolumeInfo VolumeInfo)
    {
        _logger.LogDebug("GetVolumeInfo");
        VfsVolumeInfo vol = _vfs.GetVolumeInfo();

        VolumeInfo = default;
        VolumeInfo.TotalSize = (ulong)vol.TotalBytes;
        VolumeInfo.FreeSize = (ulong)vol.FreeBytes;
        VolumeInfo.SetVolumeLabel(vol.VolumeLabel);

        return STATUS_SUCCESS;
    }

    // === Lifecycle ===

    public override Int32 Mounted(Object Host)
    {
        _logger.LogInformation("Drive mounted (WinFsp)");
        return STATUS_SUCCESS;
    }

    public override void Unmounted(Object Host)
    {
        _logger.LogInformation("Drive unmounted (WinFsp)");
    }

    public override void Cleanup(Object FileNode, Object FileDesc, String FileName, UInt32 Flags)
    {
        _logger.LogDebug("Cleanup: {Path}", FileName);
    }

    // === Read-only: all write ops return STATUS_ACCESS_DENIED ===

    public override Int32 Create(
        String FileName,
        UInt32 CreateOptions,
        UInt32 GrantedAccess,
        UInt32 FileAttributes,
        Byte[] SecurityDescriptor,
        UInt64 AllocationSize,
        out Object FileNode,
        out Object FileDesc,
        out FileInfo FileInfo,
        out String NormalizedName)
    {
        FileNode = default!;
        FileDesc = default!;
        FileInfo = default;
        NormalizedName = default!;
        return STATUS_ACCESS_DENIED;
    }

    public override Int32 Overwrite(
        Object FileNode,
        Object FileDesc,
        UInt32 FileAttributes,
        Boolean ReplaceFileAttributes,
        UInt64 AllocationSize,
        out FileInfo FileInfo)
    {
        FileInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    public override Int32 Write(
        Object FileNode,
        Object FileDesc,
        IntPtr Buffer,
        UInt64 Offset,
        UInt32 Length,
        Boolean WriteToEndOfFile,
        Boolean ConstrainedIo,
        out UInt32 BytesTransferred,
        out FileInfo FileInfo)
    {
        BytesTransferred = 0;
        FileInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    public override Int32 Flush(
        Object FileNode,
        Object FileDesc,
        out FileInfo FileInfo)
    {
        FileInfo = default;
        return STATUS_SUCCESS;
    }

    public override Int32 SetBasicInfo(
        Object FileNode,
        Object FileDesc,
        UInt32 FileAttributes,
        UInt64 CreationTime,
        UInt64 LastAccessTime,
        UInt64 LastWriteTime,
        UInt64 ChangeTime,
        out FileInfo FileInfo)
    {
        FileInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    public override Int32 SetFileSize(
        Object FileNode,
        Object FileDesc,
        UInt64 NewSize,
        Boolean SetAllocationSize,
        out FileInfo FileInfo)
    {
        FileInfo = default;
        return STATUS_ACCESS_DENIED;
    }

    public override Int32 CanDelete(
        Object FileNode,
        Object FileDesc,
        String FileName)
    {
        return STATUS_ACCESS_DENIED;
    }

    public override Int32 Rename(
        Object FileNode,
        Object FileDesc,
        String FileName,
        String NewFileName,
        Boolean ReplaceIfExists)
    {
        return STATUS_ACCESS_DENIED;
    }

    public override Int32 GetSecurity(
        Object FileNode,
        Object FileDesc,
        ref Byte[] SecurityDescriptor)
    {
        return STATUS_INVALID_DEVICE_REQUEST;
    }

    public override Int32 SetSecurity(
        Object FileNode,
        Object FileDesc,
        AccessControlSections Sections,
        Byte[] SecurityDescriptor)
    {
        return STATUS_ACCESS_DENIED;
    }

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

    // === Helpers ===

    private static FileInfo ToWinFspFileInfo(VfsFileInfo entry)
    {
        var fi = default(FileInfo);
        fi.FileAttributes = (uint)(entry.Attributes | System.IO.FileAttributes.ReadOnly);
        fi.FileSize = (ulong)entry.SizeBytes;
        fi.AllocationSize = (ulong)((entry.SizeBytes + 4095) & ~4095L); // Round up to 4K
        fi.CreationTime = (ulong)entry.CreationTimeUtc.ToFileTimeUtc();
        fi.LastAccessTime = (ulong)entry.LastAccessTimeUtc.ToFileTimeUtc();
        fi.LastWriteTime = (ulong)entry.LastWriteTimeUtc.ToFileTimeUtc();
        fi.ChangeTime = (ulong)entry.LastWriteTimeUtc.ToFileTimeUtc();
        return fi;
    }
}
