using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using DokanNet;
using LTRData.Extensions.Native.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Exceptions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Thin adapter translating DokanNet IDokanOperations2 calls to IVirtualFileSystem.
/// Read-only: all write operations return AccessDenied.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DokanFileSystemAdapter : IDokanOperations2
{
    private readonly IVirtualFileSystem _vfs;
    private readonly ILogger<DokanFileSystemAdapter> _logger;
    private readonly bool _shortCircuitShellMetadata;

    public DokanFileSystemAdapter(IVirtualFileSystem vfs, IOptions<MountSettings> mountSettings, ILogger<DokanFileSystemAdapter> logger)
    {
        _vfs = vfs;
        _logger = logger;
        _shortCircuitShellMetadata = mountSettings.Value.ShortCircuitShellMetadata;
    }

    public int DirectoryListingTimeoutResetIntervalMs => 0;

    public NtStatus CreateFile(
        ReadOnlyNativeMemory<char> fileName, DokanNet.FileAccess access, FileShare share,
        FileMode mode, FileOptions options, FileAttributes attributes, ref DokanFileInfo info)
    {
        // Short-circuit Windows shell metadata probes before allocating a string
        if (_shortCircuitShellMetadata && ShellMetadataFilter.IsShellMetadataPath(fileName.Span))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("CreateFile: short-circuit shell metadata: {Path}", fileName.Span.ToString());
            return DokanResult.FileNotFound;
        }

        string path = fileName.Span.ToString();
        _logger.LogDebug("CreateFile: {Path} mode={Mode} access={Access}", path, mode, access);

        // Reject any create/write modes
        if (mode is FileMode.CreateNew or FileMode.Create or FileMode.Append)
            return DokanResult.AccessDenied;

        try
        {
            bool isDir = _vfs.DirectoryExistsAsync(path).GetAwaiter().GetResult();
            if (isDir)
            {
                info.IsDirectory = true;
                return DokanResult.Success;
            }

            bool isFile = _vfs.FileExistsAsync(path).GetAwaiter().GetResult();
            if (isFile)
            {
                info.IsDirectory = false;
                return DokanResult.Success;
            }

            return info.IsDirectory ? DokanResult.PathNotFound : DokanResult.FileNotFound;
        }
        catch (VfsFileNotFoundException) { return DokanResult.FileNotFound; }
        catch (VfsDirectoryNotFoundException) { return DokanResult.PathNotFound; }
        catch (VfsAccessDeniedException) { return DokanResult.AccessDenied; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateFile error: {Path}", path);
            return DokanResult.InternalError;
        }
    }

    public NtStatus ReadFile(
        ReadOnlyNativeMemory<char> fileName, NativeMemory<byte> buffer,
        out int bytesRead, long offset, ref DokanFileInfo info)
    {
        string path = fileName.Span.ToString();
        _logger.LogDebug("ReadFile: {Path} offset={Offset} length={Length}", path, offset, buffer.Span.Length);
        bytesRead = 0;
        long startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            byte[] tempBuffer = new byte[buffer.Span.Length];
            int read = _vfs.ReadFileAsync(path, tempBuffer, offset).GetAwaiter().GetResult();
            tempBuffer.AsSpan(0, read).CopyTo(buffer.Span);
            bytesRead = read;

            DokanTelemetry.ReadDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                new KeyValuePair<string, object?>("result", "success"));

            return DokanResult.Success;
        }
        catch (VfsFileNotFoundException)
        {
            return DokanResult.FileNotFound;
        }
        catch (VfsAccessDeniedException)
        {
            return DokanResult.AccessDenied;
        }
        catch (Exception ex)
        {
            DokanTelemetry.ReadDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                new KeyValuePair<string, object?>("result", "error"));

            _logger.LogError(ex, "ReadFile error: {Path}", path);
            return DokanResult.InternalError;
        }
    }

    public NtStatus FindFiles(
        ReadOnlyNativeMemory<char> fileName, out IEnumerable<FindFileInformation> files,
        ref DokanFileInfo info)
    {
        string path = fileName.Span.ToString();
        _logger.LogDebug("FindFiles: {Path}", path);

        try
        {
            IReadOnlyList<VfsFileInfo> entries = _vfs.ListDirectoryAsync(path).GetAwaiter().GetResult();
            files = entries.Select(ConvertToFindFileInfo);
            return DokanResult.Success;
        }
        catch (VfsDirectoryNotFoundException)
        {
            files = [];
            return DokanResult.PathNotFound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindFiles error: {Path}", path);
            files = [];
            return DokanResult.InternalError;
        }
    }

    public NtStatus FindFilesWithPattern(
        ReadOnlyNativeMemory<char> fileName, ReadOnlyNativeMemory<char> searchPattern,
        out IEnumerable<FindFileInformation> files, ref DokanFileInfo info)
    {
        string path = fileName.Span.ToString();
        string pattern = searchPattern.Span.ToString();
        _logger.LogDebug("FindFilesWithPattern: {Path} pattern={Pattern}", path, pattern);
        try
        {
            IReadOnlyList<VfsFileInfo> entries = _vfs.ListDirectoryAsync(path).GetAwaiter().GetResult();
            files = entries
                .Where(e => DokanHelper.DokanIsNameInExpression(pattern, e.Name, true))
                .Select(ConvertToFindFileInfo);
            return DokanResult.Success;
        }
        catch (VfsDirectoryNotFoundException)
        {
            files = [];
            return DokanResult.PathNotFound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindFilesWithPattern error: {Path} pattern={Pattern}", path, pattern);
            files = [];
            return DokanResult.InternalError;
        }
    }

    public NtStatus GetFileInformation(
        ReadOnlyNativeMemory<char> fileName, out ByHandleFileInformation fileInfo,
        ref DokanFileInfo info)
    {
        string path = fileName.Span.ToString();
        _logger.LogDebug("GetFileInformation: {Path}", path);
        fileInfo = default;

        try
        {
            VfsFileInfo vfsInfo = _vfs.GetFileInfoAsync(path).GetAwaiter().GetResult();
            fileInfo = new ByHandleFileInformation
            {
                Attributes = vfsInfo.Attributes | FileAttributes.ReadOnly,
                CreationTime = vfsInfo.CreationTimeUtc,
                LastAccessTime = vfsInfo.LastAccessTimeUtc,
                LastWriteTime = vfsInfo.LastWriteTimeUtc,
                Length = vfsInfo.SizeBytes
            };
            return DokanResult.Success;
        }
        catch (VfsFileNotFoundException)
        {
            return DokanResult.FileNotFound;
        }
        catch (VfsDirectoryNotFoundException)
        {
            return DokanResult.PathNotFound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFileInformation error: {Path}", path);
            return DokanResult.InternalError;
        }
    }

    public NtStatus GetVolumeInformation(
        NativeMemory<char> volumeLabel, out FileSystemFeatures features,
        NativeMemory<char> fileSystemName, out uint maximumComponentLength,
        ref uint volumeSerialNumber, ref DokanFileInfo info)
    {
        _logger.LogDebug("GetVolumeInformation");
        volumeLabel.SetString("ZipDrive");
        fileSystemName.SetString("ZipDriveFS");
        maximumComponentLength = 256;
        features = FileSystemFeatures.CasePreservedNames
                 | FileSystemFeatures.UnicodeOnDisk
                 | FileSystemFeatures.ReadOnlyVolume;
        return DokanResult.Success;
    }

    public NtStatus GetDiskFreeSpace(
        out long freeBytesAvailable, out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes, ref DokanFileInfo info)
    {
        _logger.LogDebug("GetDiskFreeSpace");
        freeBytesAvailable = 0;
        totalNumberOfFreeBytes = 0;
        totalNumberOfBytes = 0; // Read-only, no meaningful total
        return DokanResult.Success;
    }

    public NtStatus GetFileSecurity(
        ReadOnlyNativeMemory<char> fileName, out FileSystemSecurity? security,
        AccessControlSections sections, ref DokanFileInfo info)
    {
        _logger.LogDebug("GetFileSecurity: {Path}", fileName.Span.ToString());
        security = null;
        return DokanResult.NotImplemented;
    }

    public NtStatus Mounted(ReadOnlyNativeMemory<char> mountPoint, ref DokanFileInfo info)
    {
        _logger.LogInformation("Drive mounted at {MountPoint}", mountPoint.Span.ToString());
        return DokanResult.Success;
    }

    public NtStatus Unmounted(ref DokanFileInfo info)
    {
        _logger.LogInformation("Drive unmounted");
        return DokanResult.Success;
    }

    // === No-op lifecycle methods ===

    public void Cleanup(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
    {
        _logger.LogDebug("Cleanup: {Path}", fileName.Span.ToString());
    }

    public void CloseFile(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
    {
        _logger.LogDebug("CloseFile: {Path}", fileName.Span.ToString());
    }

    // === Read-only: all write ops return AccessDenied ===

    public NtStatus WriteFile(ReadOnlyNativeMemory<char> fileName, ReadOnlyNativeMemory<byte> buffer,
        out int bytesWritten, long offset, ref DokanFileInfo info)
    {
        _logger.LogDebug("WriteFile: {Path}", fileName.Span.ToString());
        bytesWritten = 0;
        return DokanResult.AccessDenied;
    }

    public NtStatus DeleteFile(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
    {
        _logger.LogDebug("DeleteFile: {Path}", fileName.Span.ToString());
        return DokanResult.AccessDenied;
    }

    public NtStatus DeleteDirectory(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
    {
        _logger.LogDebug("DeleteDirectory: {Path}", fileName.Span.ToString());
        return DokanResult.AccessDenied;
    }

    public NtStatus MoveFile(ReadOnlyNativeMemory<char> oldName, ReadOnlyNativeMemory<char> newName,
        bool replace, ref DokanFileInfo info)
    {
        _logger.LogDebug("MoveFile: {OldPath} -> {NewPath}", oldName.Span.ToString(), newName.Span.ToString());
        return DokanResult.AccessDenied;
    }

    public NtStatus SetFileAttributes(ReadOnlyNativeMemory<char> fileName,
        FileAttributes attributes, ref DokanFileInfo info)
    {
        _logger.LogDebug("SetFileAttributes: {Path}", fileName.Span.ToString());
        return DokanResult.AccessDenied;
    }

    public NtStatus SetFileTime(ReadOnlyNativeMemory<char> fileName,
        DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, ref DokanFileInfo info)
    {
        _logger.LogDebug("SetFileTime: {Path}", fileName.Span.ToString());
        return DokanResult.AccessDenied;
    }

    public NtStatus SetEndOfFile(ReadOnlyNativeMemory<char> fileName, long length, ref DokanFileInfo info)
    {
        _logger.LogDebug("SetEndOfFile: {Path}", fileName.Span.ToString());
        return DokanResult.AccessDenied;
    }

    public NtStatus SetAllocationSize(ReadOnlyNativeMemory<char> fileName, long length, ref DokanFileInfo info)
    {
        _logger.LogDebug("SetAllocationSize: {Path}", fileName.Span.ToString());
        return DokanResult.AccessDenied;
    }

    public NtStatus SetFileSecurity(ReadOnlyNativeMemory<char> fileName,
        FileSystemSecurity security, AccessControlSections sections, ref DokanFileInfo info)
    {
        _logger.LogDebug("SetFileSecurity: {Path}", fileName.Span.ToString());
        return DokanResult.AccessDenied;
    }

    public NtStatus FlushFileBuffers(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
    {
        _logger.LogDebug("FlushFileBuffers: {Path}", fileName.Span.ToString());
        return DokanResult.Success;
    }

    public NtStatus LockFile(ReadOnlyNativeMemory<char> fileName, long offset, long length, ref DokanFileInfo info)
    {
        _logger.LogDebug("LockFile: {Path} offset={Offset} length={Length}", fileName.Span.ToString(), offset, length);
        return DokanResult.NotImplemented;
    }

    public NtStatus UnlockFile(ReadOnlyNativeMemory<char> fileName, long offset, long length, ref DokanFileInfo info)
    {
        _logger.LogDebug("UnlockFile: {Path} offset={Offset} length={Length}", fileName.Span.ToString(), offset, length);
        return DokanResult.NotImplemented;
    }

    public NtStatus FindStreams(ReadOnlyNativeMemory<char> fileName,
        out IEnumerable<FindFileInformation> streams, ref DokanFileInfo info)
    {
        _logger.LogDebug("FindStreams: {Path}", fileName.Span.ToString());
        streams = [];
        return DokanResult.NotImplemented;
    }

    // === Helpers ===

    private static FindFileInformation ConvertToFindFileInfo(VfsFileInfo entry) => new()
    {
        FileName = entry.Name.AsMemory(),
        Attributes = entry.Attributes | FileAttributes.ReadOnly,
        CreationTime = entry.CreationTimeUtc,
        LastAccessTime = entry.LastAccessTimeUtc,
        LastWriteTime = entry.LastWriteTimeUtc,
        Length = entry.SizeBytes
    };

    private NtStatus WrapException(string path, Func<NtStatus> action)
    {
        try
        {
            return action();
        }
        catch (VfsFileNotFoundException)
        {
            return DokanResult.FileNotFound;
        }
        catch (VfsDirectoryNotFoundException)
        {
            return DokanResult.PathNotFound;
        }
        catch (VfsAccessDeniedException)
        {
            return DokanResult.AccessDenied;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error for path: {Path}", path);
            return DokanResult.InternalError;
        }
    }
}
