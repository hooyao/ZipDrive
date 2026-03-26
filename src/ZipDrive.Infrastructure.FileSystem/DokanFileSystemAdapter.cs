using System.Buffers;
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
/// Supports atomic VFS replacement via <see cref="SwapAsync"/> with drain mechanism.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DokanFileSystemAdapter : IDokanOperations2
{
    private volatile IVirtualFileSystem? _vfs;
    private bool _draining;
    private int _activeCount;
    private TaskCompletionSource? _drainTcs;

    private readonly ILogger<DokanFileSystemAdapter> _logger;
    private readonly bool _shortCircuitShellMetadata;

    // NtStatus constant for DeviceBusy — Dokan returns this when the device is temporarily unavailable.
    private static readonly NtStatus DeviceBusy = (NtStatus)0xC00000AE;

    public DokanFileSystemAdapter(IOptions<MountSettings> mountSettings, ILogger<DokanFileSystemAdapter> logger)
    {
        _logger = logger;
        _shortCircuitShellMetadata = mountSettings.Value.ShortCircuitShellMetadata;
    }

    /// <summary>
    /// Sets the initial VFS reference. Must be called before Dokan starts dispatching callbacks.
    /// </summary>
    public void SetVfs(IVirtualFileSystem vfs)
    {
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
    }

    /// <summary>
    /// Atomically swaps the VFS reference with drain mechanism.
    /// Activates drain mode → waits for in-flight ops to complete (or timeout) → swaps → deactivates drain.
    /// Returns the old VFS reference for caller to dispose.
    /// </summary>
    public async Task<IVirtualFileSystem?> SwapAsync(IVirtualFileSystem newVfs, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(newVfs);

        _drainTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Volatile.Write(ref _draining, true);

        // Check if already drained (no in-flight ops)
        if (Volatile.Read(ref _activeCount) == 0)
            _drainTcs.TrySetResult();

        var drained = await Task.WhenAny(_drainTcs.Task, Task.Delay(timeout)) == _drainTcs.Task;

        if (!drained)
        {
            _logger.LogWarning(
                "Drain timeout after {Timeout}s, {Count} ops still active. Forcing swap.",
                timeout.TotalSeconds, Volatile.Read(ref _activeCount));
        }
        else
        {
            _logger.LogInformation("Drain completed, swapping VFS");
        }

        var old = Interlocked.Exchange(ref _vfs, newVfs);

        Volatile.Write(ref _draining, false);

        return old;
    }

    /// <summary>Current number of in-flight Dokan callbacks.</summary>
    public int ActiveCount => Volatile.Read(ref _activeCount);

    // === Drain guard helpers ===

    /// <summary>
    /// Attempts to enter the drain guard. Returns the VFS snapshot if allowed, null if draining.
    /// Caller MUST call ExitGuard() in a finally block if this returns non-null.
    /// </summary>
    private IVirtualFileSystem? EnterGuard()
    {
        if (Volatile.Read(ref _draining))
            return null;

        Interlocked.Increment(ref _activeCount);

        // Double-check: drain may have activated between the check and the increment
        if (Volatile.Read(ref _draining))
        {
            if (Interlocked.Decrement(ref _activeCount) == 0)
                _drainTcs?.TrySetResult();
            return null;
        }

        return _vfs;
    }

    /// <summary>
    /// Exits the drain guard. Signals drain completion if this was the last in-flight op.
    /// </summary>
    private void ExitGuard()
    {
        if (Interlocked.Decrement(ref _activeCount) == 0 && Volatile.Read(ref _draining))
            _drainTcs?.TrySetResult();
    }

    // === Public guarded methods for managed callers (tests, non-Dokan consumers) ===

    /// <summary>
    /// Enters the drain guard with async retry. Mirrors Explorer's auto-retry on DeviceBusy.
    /// Retries for up to ~10 seconds to accommodate drain windows during VFS swap.
    /// </summary>
    private async Task<IVirtualFileSystem> EnterGuardAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            var vfs = EnterGuard();
            if (vfs is not null) return vfs;
            await Task.Delay(20, ct);
        }
        throw new InvalidOperationException("VFS drain did not complete within retry window (10s)");
    }

    public async Task<bool> GuardedFileExistsAsync(string path, CancellationToken ct = default)
    {
        var vfs = await EnterGuardAsync(ct);
        try { return await vfs.FileExistsAsync(path, ct); }
        finally { ExitGuard(); }
    }

    public async Task<bool> GuardedDirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        var vfs = await EnterGuardAsync(ct);
        try { return await vfs.DirectoryExistsAsync(path, ct); }
        finally { ExitGuard(); }
    }

    public async Task<IReadOnlyList<VfsFileInfo>> GuardedListDirectoryAsync(string path, CancellationToken ct = default)
    {
        var vfs = await EnterGuardAsync(ct);
        try { return await vfs.ListDirectoryAsync(path, ct); }
        finally { ExitGuard(); }
    }

    public async Task<int> GuardedReadFileAsync(string path, byte[] buffer, long offset, CancellationToken ct = default)
    {
        var vfs = await EnterGuardAsync(ct);
        try { return await vfs.ReadFileAsync(path, buffer, offset, ct); }
        finally { ExitGuard(); }
    }

    public async Task<VfsFileInfo> GuardedGetFileInfoAsync(string path, CancellationToken ct = default)
    {
        var vfs = await EnterGuardAsync(ct);
        try { return await vfs.GetFileInfoAsync(path, ct); }
        finally { ExitGuard(); }
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

        // Reject any create/write modes before entering guard
        if (mode is FileMode.CreateNew or FileMode.Create or FileMode.Append)
            return DokanResult.AccessDenied;

        var vfs = EnterGuard();
        if (vfs is null)
            return DeviceBusy;

        try
        {
            string path = fileName.Span.ToString();
            _logger.LogDebug("CreateFile: {Path} mode={Mode} access={Access}", path, mode, access);

            bool isDir = vfs.DirectoryExistsAsync(path).GetAwaiter().GetResult();
            if (isDir)
            {
                info.IsDirectory = true;
                return DokanResult.Success;
            }

            bool isFile = vfs.FileExistsAsync(path).GetAwaiter().GetResult();
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
            _logger.LogError(ex, "CreateFile error: {Path}", fileName.Span.ToString());
            return DokanResult.InternalError;
        }
        finally
        {
            ExitGuard();
        }
    }

    public NtStatus ReadFile(
        ReadOnlyNativeMemory<char> fileName, NativeMemory<byte> buffer,
        out int bytesRead, long offset, ref DokanFileInfo info)
    {
        bytesRead = 0;

        var vfs = EnterGuard();
        if (vfs is null)
            return DeviceBusy;

        long startTimestamp = Stopwatch.GetTimestamp();
        int requestedLength = buffer.Span.Length;
        byte[] rentedArray = ArrayPool<byte>.Shared.Rent(requestedLength);
        try
        {
            string path = fileName.Span.ToString();
            _logger.LogDebug("ReadFile: {Path} offset={Offset} length={Length}", path, offset, buffer.Span.Length);

            int read = vfs.ReadFileAsync(path, rentedArray, offset).GetAwaiter().GetResult();
            bytesRead = Math.Min(read, requestedLength);

            if (bytesRead > 0)
                rentedArray.AsSpan(0, bytesRead).CopyTo(buffer.Span);

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

            _logger.LogError(ex, "ReadFile error: {Path}", fileName.Span.ToString());
            return DokanResult.InternalError;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedArray);
            ExitGuard();
        }
    }

    public NtStatus FindFiles(
        ReadOnlyNativeMemory<char> fileName, out IEnumerable<FindFileInformation> files,
        ref DokanFileInfo info)
    {
        files = [];

        var vfs = EnterGuard();
        if (vfs is null)
            return DeviceBusy;

        try
        {
            string path = fileName.Span.ToString();
            _logger.LogDebug("FindFiles: {Path}", path);

            IReadOnlyList<VfsFileInfo> entries = vfs.ListDirectoryAsync(path).GetAwaiter().GetResult();
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
            _logger.LogError(ex, "FindFiles error: {Path}", fileName.Span.ToString());
            files = [];
            return DokanResult.InternalError;
        }
        finally
        {
            ExitGuard();
        }
    }

    public NtStatus FindFilesWithPattern(
        ReadOnlyNativeMemory<char> fileName, ReadOnlyNativeMemory<char> searchPattern,
        out IEnumerable<FindFileInformation> files, ref DokanFileInfo info)
    {
        files = [];

        var vfs = EnterGuard();
        if (vfs is null)
            return DeviceBusy;

        try
        {
            string path = fileName.Span.ToString();
            string pattern = searchPattern.Span.ToString();
            _logger.LogDebug("FindFilesWithPattern: {Path} pattern={Pattern}", path, pattern);

            IReadOnlyList<VfsFileInfo> entries = vfs.ListDirectoryAsync(path).GetAwaiter().GetResult();
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
            _logger.LogError(ex, "FindFilesWithPattern error: {Path}", fileName.Span.ToString());
            files = [];
            return DokanResult.InternalError;
        }
        finally
        {
            ExitGuard();
        }
    }

    public NtStatus GetFileInformation(
        ReadOnlyNativeMemory<char> fileName, out ByHandleFileInformation fileInfo,
        ref DokanFileInfo info)
    {
        fileInfo = default;

        var vfs = EnterGuard();
        if (vfs is null)
            return DeviceBusy;

        try
        {
            string path = fileName.Span.ToString();
            _logger.LogDebug("GetFileInformation: {Path}", path);

            VfsFileInfo vfsInfo = vfs.GetFileInfoAsync(path).GetAwaiter().GetResult();
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
            _logger.LogError(ex, "GetFileInformation error: {Path}", fileName.Span.ToString());
            return DokanResult.InternalError;
        }
        finally
        {
            ExitGuard();
        }
    }

    // === Stateless callbacks — no guard needed ===

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

    // === No-op lifecycle methods — no guard needed (lightweight, no VFS access) ===

    public void Cleanup(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
    {
        _logger.LogDebug("Cleanup: {Path}", fileName.Span.ToString());
    }

    public void CloseFile(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
    {
        _logger.LogDebug("CloseFile: {Path}", fileName.Span.ToString());
    }

    // === Read-only: all write ops return AccessDenied — no guard needed ===

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

}
