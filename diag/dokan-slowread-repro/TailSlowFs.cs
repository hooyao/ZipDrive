using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using DokanNet;
using FileAccess = DokanNet.FileAccess;

namespace DokanSlowReadRepro;

/// <summary>
/// Dokan mirror of the WinFsp <c>TailSlowFs</c> (diag/winfsp-slowread-repro/SerializeExperiment.cs).
///
/// Read-only in-memory volume with two files:
///   \video.bin  — a "tail" read (last 1 MB) blocks for <see cref="_slowTailDelayMs"/> ms,
///                 simulating ZipDrive waiting on sequential chunk extraction.
///   \other.bin  — always returns instantly.
///
/// The experiment asks: while a slow tail read of \video.bin is in flight, does a CONCURRENT
/// open+read of the SAME file (a second handle) get blocked?  On WinFsp it does (~2850 ms).
/// The hypothesis under test is that Dokan does NOT block it, because the Dokan FSD releases
/// the FCB lock before pending the read (dokany/sys/read.c:242), whereas WinFsp holds a
/// FileNode shared lock across the whole user-mode round-trip (winfsp/src/sys/read.c:444/671).
///
/// IMPORTANT — confound to rule out: Dokan's ReadFile callback is SYNCHRONOUS. A blocking wait
/// here pins one dispatcher thread for the whole delay. The serialize experiment issues only ONE
/// slow read at a time, so as long as Dokan has >= 2 dispatcher threads, a same-file block (if
/// observed) is attributable to the kernel FCB lock, not to dispatcher-thread exhaustion. We test
/// several thread counts to separate the two.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TailSlowFs : IDokanOperations
{
    private readonly long _size;
    private readonly int _slowTailDelayMs;
    private readonly bool _partialFallback;
    private readonly byte[] _content;

    public TailSlowFs(long size, int slowTailDelayMs, bool partialFallback = false)
    {
        _size = size;
        _slowTailDelayMs = slowTailDelayMs;
        _partialFallback = partialFallback;
        _content = new byte[(int)Math.Min(size, 64 * 1024)];
        Array.Fill(_content, (byte)0xCD);
    }

    private static bool IsVideo(string f) => string.Equals(f, "\\video.bin", StringComparison.OrdinalIgnoreCase);
    private static bool IsOther(string f) => string.Equals(f, "\\other.bin", StringComparison.OrdinalIgnoreCase);
    private static bool IsFile(string f) => IsVideo(f) || IsOther(f);
    private static bool IsRoot(string f) => f == "\\";

    // ── open / lifecycle ──
    public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode,
        FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        // Read-only: reject anything that isn't a plain open of an existing object.
        if (mode is not (FileMode.Open or FileMode.OpenOrCreate))
            return DokanResult.AccessDenied;

        if (IsRoot(fileName))
        {
            info.IsDirectory = true;
            return DokanResult.Success;
        }
        if (IsFile(fileName))
        {
            info.IsDirectory = false;
            return DokanResult.Success;
        }
        return DokanResult.FileNotFound;
    }

    public void Cleanup(string fileName, IDokanFileInfo info) { }
    public void CloseFile(string fileName, IDokanFileInfo info) { }

    // ── the read under test ──
    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        if (offset >= _size)
            return DokanResult.Success; // EOF => 0 bytes

        bool isTail = offset >= _size - 1024 * 1024; // last 1 MB
        if (IsVideo(fileName) && isTail && _slowTailDelayMs > 0)
        {
            if (_partialFallback)
            {
                // PROPOSED P0 FIX analogue: wait a short budget, then return a partial 4 KB.
                // A partial read is legal on Windows; the consumer re-issues for the rest.
                Thread.Sleep(300);
                int partial = Math.Min(buffer.Length, 4096);
                int copyP = Math.Min(partial, _content.Length);
                Array.Copy(_content, 0, buffer, 0, copyP);
                if (copyP < partial) Array.Clear(buffer, copyP, partial - copyP);
                bytesRead = partial;
                return DokanResult.Success;
            }

            // Current ZipDrive behavior: block for the full "extraction".
            Thread.Sleep(_slowTailDelayMs);
        }

        int n = (int)Math.Min((long)buffer.Length, _size - offset);
        int copy = Math.Min(n, _content.Length);
        Array.Copy(_content, 0, buffer, 0, copy);
        if (copy < n) Array.Clear(buffer, copy, n - copy);
        bytesRead = n;
        return DokanResult.Success;
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        if (IsRoot(fileName) || info.IsDirectory)
        {
            fileInfo = Dir(fileName);
            return DokanResult.Success;
        }
        if (IsFile(fileName))
        {
            fileInfo = FileInfo(fileName);
            return DokanResult.Success;
        }
        fileInfo = default;
        return DokanResult.FileNotFound;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = new List<FileInformation>
        {
            FileInfo("video.bin"),
            FileInfo("other.bin"),
        };
        return DokanResult.Success;
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = new List<FileInformation>();
        return DokanResult.NotImplemented; // fall back to FindFiles
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = "Serialize";
        features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk |
                   FileSystemFeatures.ReadOnlyVolume;
        fileSystemName = "NTFS";
        maximumComponentLength = 255;
        return DokanResult.Success;
    }

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        totalNumberOfBytes = 1L << 30;
        freeBytesAvailable = 0;
        totalNumberOfFreeBytes = 0;
        return DokanResult.Success;
    }

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info) => DokanResult.Success;
    public NtStatus Unmounted(IDokanFileInfo info) => DokanResult.Success;

    // ── read-only: everything mutating is denied / not implemented ──
    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    { bytesWritten = 0; return DokanResult.AccessDenied; }
    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info) => DokanResult.Success;
    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info) => DokanResult.AccessDenied;
    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info) => DokanResult.AccessDenied;
    public NtStatus DeleteFile(string fileName, IDokanFileInfo info) => DokanResult.AccessDenied;
    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info) => DokanResult.AccessDenied;
    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info) => DokanResult.AccessDenied;
    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info) => DokanResult.AccessDenied;
    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info) => DokanResult.AccessDenied;
    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;
    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;
    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info)
    { security = null; return DokanResult.NotImplemented; }
    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info) => DokanResult.AccessDenied;
    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    { streams = new List<FileInformation>(); return DokanResult.NotImplemented; }

    // ── helpers ──
    private FileInformation FileInfo(string name)
    {
        DateTime now = DateTime.UtcNow;
        return new FileInformation
        {
            FileName = name.TrimStart('\\'),
            Attributes = FileAttributes.Archive | FileAttributes.ReadOnly,
            Length = _size,
            CreationTime = now,
            LastAccessTime = now,
            LastWriteTime = now,
        };
    }

    private FileInformation Dir(string name)
    {
        DateTime now = DateTime.UtcNow;
        return new FileInformation
        {
            FileName = name.TrimStart('\\'),
            Attributes = FileAttributes.Directory,
            CreationTime = now,
            LastAccessTime = now,
            LastWriteTime = now,
        };
    }
}
