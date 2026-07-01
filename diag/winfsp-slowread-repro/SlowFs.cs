using System.Runtime.Versioning;
using WinFsp.Native;

namespace SlowReadRepro;

/// <summary>
/// Minimal in-memory WinFsp file system to test ONE thing:
/// does a slow read of one file block fast reads of OTHER files?
///
/// Layout (read-only):
///   \slow.bin        — reading this delays by <see cref="SlowDelayMs"/> using <see cref="Mode"/>
///   \fast-0..N.bin   — return instantly
///
/// This mirrors ZipDrive's situation: video files are "slow reads" (block on chunk
/// extraction) while image files are "fast reads". We vary HOW the slow read waits
/// to find out which pattern reproduces the whole-volume freeze.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SlowFs : IFileSystem
{
    internal enum SlowMode
    {
        AsyncDelay,   // await Task.Delay — truly async, returns incomplete ValueTask (ZipDrive's intended path)
        ThreadSleep,  // Thread.Sleep — blocks the dispatcher thread synchronously
        SyncOverAsync,// Task.Delay(...).Wait() — sync-over-async, blocks dispatcher thread (suspected ZipDrive reality)
        SlowInOpen    // delay in OpenFile (a SYNC callback with no STATUS_PENDING path) instead of Read
    }

    private const int FileSize = 64 * 1024; // 64 KB per file
    private readonly int _fastCount;
    private readonly int _slowDelayMs;
    private readonly SlowMode _mode;
    private readonly byte[] _content = new byte[FileSize];

    public SlowFs(int fastCount, int slowDelayMs, SlowMode mode)
    {
        _fastCount = fastCount;
        _slowDelayMs = slowDelayMs;
        _mode = mode;
        Array.Fill(_content, (byte)0xAB);
    }

    public bool SynchronousIo => false; // same as ZipDrive's WinFspFileSystemAdapter

    public int Init(FileSystemHost host)
    {
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 0;          // same as ZipDrive (kernel FileInfo cache OFF)
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        host.VolumeSerialNumber = 0x53524550; // "SREP"
        host.FileSystemName = "NTFS";
        return NtStatus.Success;
    }

    public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
    {
        totalSize = 1UL << 30;
        freeSize = 0;
        volumeLabel = "SlowRepro";
        return NtStatus.Success;
    }

    // ── name lookup: synchronous, no STATUS_PENDING path ──
    public ValueTask<SecurityByNameResult> GetFileSecurityByName(
        string fileName, bool getSecurityDescriptor, CancellationToken ct)
    {
        if (fileName == "\\" )
            return new(SecurityByNameResult.Success((uint)FileAttributes.Directory));
        if (IsKnownFile(fileName))
            return new(SecurityByNameResult.Success((uint)FileAttributes.Archive));
        return new(SecurityByNameResult.Error(NtStatus.ObjectNameNotFound));
    }

    public ValueTask<CreateResult> CreateFile(
        string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[]? securityDescriptor, ulong allocationSize,
        FileOperationInfo info, CancellationToken ct)
        => new(CreateResult.Error(NtStatus.AccessDenied)); // read-only volume

    public ValueTask<CreateResult> OpenFile(
        string fileName, uint createOptions, uint grantedAccess,
        FileOperationInfo info, CancellationToken ct)
    {
        if (fileName == "\\")
        {
            info.IsDirectory = true;
            return new(new CreateResult(NtStatus.Success, MakeDirInfo()));
        }
        if (IsKnownFile(fileName))
        {
            info.IsDirectory = false;
            bool isSlow = string.Equals(fileName, "\\slow.bin", StringComparison.OrdinalIgnoreCase);
            if (isSlow && _slowDelayMs > 0 && _mode == SlowMode.SlowInOpen)
            {
                // OpenFile is a synchronous callback with NO STATUS_PENDING path:
                // a blocking wait here pins the dispatcher thread for the whole delay.
                Thread.Sleep(_slowDelayMs);
            }
            return new(new CreateResult(NtStatus.Success, MakeFileInfo()));
        }
        return new(CreateResult.Error(NtStatus.ObjectNameNotFound));
    }

    // ── the read under test ──
    public async ValueTask<ReadResult> ReadFile(
        string fileName, Memory<byte> buffer, ulong offset,
        FileOperationInfo info, CancellationToken ct)
    {
        if (offset >= FileSize)
            return ReadResult.EndOfFile();

        bool isSlow = string.Equals(fileName, "\\slow.bin", StringComparison.OrdinalIgnoreCase);
        if (isSlow && _slowDelayMs > 0)
        {
            switch (_mode)
            {
                case SlowMode.AsyncDelay:
                    await Task.Delay(_slowDelayMs, ct).ConfigureAwait(false);
                    break;
                case SlowMode.ThreadSleep:
                    Thread.Sleep(_slowDelayMs);
                    break;
                case SlowMode.SyncOverAsync:
                    Task.Delay(_slowDelayMs, ct).Wait(ct);
                    break;
            }
        }

        int n = (int)Math.Min((ulong)buffer.Length, FileSize - offset);
        _content.AsSpan((int)offset, n).CopyTo(buffer.Span);
        return ReadResult.Success((uint)n);
    }

    public ValueTask<WriteResult> WriteFile(
        string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
        bool writeToEndOfFile, bool constrainedIo,
        FileOperationInfo info, CancellationToken ct)
        => new(WriteResult.Error(NtStatus.AccessDenied));

    public ValueTask<FsResult> GetFileInformation(
        string fileName, FileOperationInfo info, CancellationToken ct)
    {
        if (info.IsDirectory || fileName == "\\")
            return new(FsResult.Success(MakeDirInfo()));
        if (IsKnownFile(fileName))
            return new(FsResult.Success(MakeFileInfo()));
        return new(FsResult.Error(NtStatus.ObjectNameNotFound));
    }

    public ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
        => new(NtStatus.AccessDenied);

    public unsafe ValueTask<ReadDirectoryResult> ReadDirectory(
        string fileName, string? pattern, string? marker,
        nint buffer, uint length,
        FileOperationInfo info, CancellationToken ct)
    {
        uint bytesTransferred = 0;
        // enumerate: slow.bin + fast-0..N.bin, honoring marker for resumption
        var names = new List<string> { "slow.bin" };
        for (int i = 0; i < _fastCount; i++) names.Add($"fast-{i}.bin");

        bool passedMarker = string.IsNullOrEmpty(marker);
        foreach (var name in names)
        {
            if (!passedMarker)
            {
                if (string.Equals(name, marker, StringComparison.OrdinalIgnoreCase))
                    passedMarker = true;
                continue;
            }
            if (!AddEntry(name, buffer, length, &bytesTransferred))
                return new(ReadDirectoryResult.Success(bytesTransferred)); // buffer full
        }
        WinFspFileSystem.EndDirInfo(buffer, length, &bytesTransferred);
        return new(ReadDirectoryResult.Success(bytesTransferred));
    }

    private unsafe bool AddEntry(string name, nint buffer, uint length, uint* pBytesTransferred)
    {
        var di = new FspDirInfo();
        di.FileInfo = MakeFileInfoStruct();
        di.SetFileName(name);
        return WinFspFileSystem.AddDirInfo(&di, buffer, length, pBytesTransferred);
    }

    private bool IsKnownFile(string fileName)
    {
        if (string.Equals(fileName, "\\slow.bin", StringComparison.OrdinalIgnoreCase)) return true;
        if (!fileName.StartsWith("\\fast-", StringComparison.OrdinalIgnoreCase)) return false;
        if (!fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static FspFileInfo MakeFileInfoStruct()
    {
        ulong now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        return new FspFileInfo
        {
            FileAttributes = (uint)FileAttributes.Archive,
            FileSize = FileSize,
            AllocationSize = FileSize,
            CreationTime = now,
            LastAccessTime = now,
            LastWriteTime = now,
            ChangeTime = now,
            IndexNumber = 1,
            HardLinks = 0,
        };
    }

    private static FspFileInfo MakeDirInfoStruct()
    {
        ulong now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        return new FspFileInfo
        {
            FileAttributes = (uint)FileAttributes.Directory,
            FileSize = 0,
            AllocationSize = 0,
            CreationTime = now,
            LastAccessTime = now,
            LastWriteTime = now,
            ChangeTime = now,
            IndexNumber = 0,
            HardLinks = 0,
        };
    }

    private static FspFileInfo MakeFileInfo() => MakeFileInfoStruct();
    private static FspFileInfo MakeDirInfo() => MakeDirInfoStruct();
}
