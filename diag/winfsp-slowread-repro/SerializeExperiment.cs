using System.Diagnostics;
using System.Runtime.Versioning;
using WinFsp.Native;

namespace SlowReadRepro;

/// <summary>
/// Focused experiment #2: does WinFsp serialize reads to the SAME file?
///
/// The WinFsp author states: with non-overlapped handles, the Windows I/O Manager
/// takes an FCB lock and serializes all I/O to a file object; Dokan does not.
/// This tests whether a single slow read of a file blocks a CONCURRENT read of the
/// SAME file (different handle) and/or reads of OTHER files.
///
/// Run:  SlowReadRepro serialize [--threadCount=N]
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SerializeExperiment
{
    const int FileSize = 8 * 1024 * 1024; // 8 MB so a "tail" read is meaningfully far in

    public static void Run(int threadCountArg)
    {
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine("EXPERIMENT: same-file read serialization (WinFsp vs expectation)");
        Console.WriteLine($"  threadCount = {(threadCountArg == 0 ? "default" : threadCountArg.ToString())}");

        RunOnce("BLOCKING tail read (current ZipDrive behavior)", slowTailDelayMs: 3000, partialFallback: false, threadCountArg);
        RunOnce("PARTIAL-RETURN tail read (proposed P0 fix)",     slowTailDelayMs: 3000, partialFallback: true,  threadCountArg);
    }

    static void RunOnce(string title, int slowTailDelayMs, bool partialFallback, int threadCountArg)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} ──");
        var fs = new TailSlowFs(FileSize, slowTailDelayMs, partialFallback);
        var host = new FileSystemHost(fs) { Prefix = $@"\winfsp-serialize\{(partialFallback ? "fix" : "block")}-{Environment.ProcessId}" };
        int mr = host.MountEx(null, (uint)threadCountArg);
        if (mr < 0) { Console.WriteLine($"  MOUNT FAILED 0x{mr:X8}"); return; }
        string root = host.MountPoint!;
        if (!root.EndsWith('\\')) root += "\\";

        try
        {
            // Kick off ONE tail read of video.bin (the "Photos reads moov" read).
            var slowStarted = new ManualResetEventSlim();
            var slowTask = Task.Run(() =>
            {
                using var h = File.OpenHandle(root + "video.bin", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[64 * 1024];
                slowStarted.Set();
                var t0 = Stopwatch.GetTimestamp();
                RandomAccess.Read(h, buf, FileSize - buf.Length);
                return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            });

            slowStarted.Wait();
            Thread.Sleep(150);

            double sameFileHead = TimeRead(root + "video.bin", offset: 0, asyncHandle: false);
            double slowMs = slowTask.GetAwaiter().GetResult();

            Console.WriteLine($"  tail read (video.bin)           : {slowMs,8:F1}ms");
            Console.WriteLine($"  concurrent HEAD SAME file (sync): {sameFileHead,8:F1}ms  {(sameFileHead > 500 ? "<== BLOCKED (Photos would freeze)" : "OK (Photos stays responsive)")}");
        }
        finally { host.Dispose(); }
    }

    static double TimeRead(string path, long offset, bool asyncHandle)
    {
        var buf = new byte[64 * 1024];
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            var opts = asyncHandle ? FileOptions.Asynchronous : FileOptions.None;
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, opts);
            if (asyncHandle)
                RandomAccess.ReadAsync(h, buf, offset).AsTask().GetAwaiter().GetResult();
            else
                RandomAccess.Read(h, buf, offset);
        }
        catch { }
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }
}

/// <summary>FS with two files: video.bin (slow on tail reads) and other.bin (always fast).</summary>
[SupportedOSPlatform("windows")]
internal sealed class TailSlowFs : IFileSystem
{
    private readonly int _size;
    private readonly int _slowTailDelayMs;
    private readonly bool _partialFallback;
    private readonly byte[] _content;

    public TailSlowFs(int size, int slowTailDelayMs, bool partialFallback = false)
    {
        _size = size; _slowTailDelayMs = slowTailDelayMs; _partialFallback = partialFallback;
        _content = new byte[Math.Min(size, 64 * 1024)];
        Array.Fill(_content, (byte)0xCD);
    }

    public bool SynchronousIo => false;

    public int Init(FileSystemHost host)
    {
        host.SectorSize = 4096; host.SectorsPerAllocationUnit = 1; host.MaxComponentLength = 255;
        host.FileInfoTimeout = 0; host.CasePreservedNames = true; host.UnicodeOnDisk = true;
        host.VolumeSerialNumber = 0x53455249; host.FileSystemName = "NTFS";
        return NtStatus.Success;
    }

    public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
    { totalSize = 1UL << 30; freeSize = 0; volumeLabel = "Serialize"; return NtStatus.Success; }

    public ValueTask<SecurityByNameResult> GetFileSecurityByName(string fileName, bool g, CancellationToken ct)
        => new(fileName == "\\"
            ? SecurityByNameResult.Success((uint)FileAttributes.Directory)
            : IsFile(fileName) ? SecurityByNameResult.Success((uint)FileAttributes.Archive)
                               : SecurityByNameResult.Error(NtStatus.ObjectNameNotFound));

    public ValueTask<CreateResult> CreateFile(string f, uint c, uint g, uint a, byte[]? s, ulong al, FileOperationInfo i, CancellationToken ct)
        => new(CreateResult.Error(NtStatus.AccessDenied));

    public ValueTask<CreateResult> OpenFile(string fileName, uint co, uint ga, FileOperationInfo info, CancellationToken ct)
    {
        if (fileName == "\\") { info.IsDirectory = true; return new(new CreateResult(NtStatus.Success, Dir())); }
        if (IsFile(fileName)) { info.IsDirectory = false; return new(new CreateResult(NtStatus.Success, FileInfo())); }
        return new(CreateResult.Error(NtStatus.ObjectNameNotFound));
    }

    public async ValueTask<ReadResult> ReadFile(string fileName, Memory<byte> buffer, ulong offset, FileOperationInfo info, CancellationToken ct)
    {
        if (offset >= (ulong)_size) return ReadResult.EndOfFile();
        bool isVideo = string.Equals(fileName, "\\video.bin", StringComparison.OrdinalIgnoreCase);
        bool isTail = offset >= (ulong)(_size - 1024 * 1024); // last 1 MB
        if (isVideo && isTail && _slowTailDelayMs > 0)
        {
            if (_partialFallback)
            {
                // PROPOSED P0 FIX: don't hold the read for the whole extraction. Wait a short
                // budget, then return whatever is ready (here: a partial 4 KB). A partial read is
                // legal on Windows — the consumer re-issues for the rest. The FCB lock is released
                // quickly, so same-file reads (Photos) never stall for tens of seconds.
                await Task.Delay(300, ct).ConfigureAwait(false);
                int partial = Math.Min(buffer.Length, 4096);
                _content.AsSpan(0, Math.Min(partial, _content.Length)).CopyTo(buffer.Span);
                return ReadResult.Success((uint)partial);
            }
            await Task.Delay(_slowTailDelayMs, ct).ConfigureAwait(false); // current: block for full extraction
        }

        int n = (int)Math.Min((ulong)buffer.Length, (ulong)_size - offset);
        int copy = Math.Min(n, _content.Length);
        _content.AsSpan(0, copy).CopyTo(buffer.Span);
        if (copy < n) buffer.Span.Slice(copy, n - copy).Clear();
        return ReadResult.Success((uint)n);
    }

    public ValueTask<WriteResult> WriteFile(string f, ReadOnlyMemory<byte> b, ulong o, bool e, bool c, FileOperationInfo i, CancellationToken ct)
        => new(WriteResult.Error(NtStatus.AccessDenied));

    public ValueTask<FsResult> GetFileInformation(string fileName, FileOperationInfo info, CancellationToken ct)
        => new(info.IsDirectory || fileName == "\\" ? FsResult.Success(Dir())
             : IsFile(fileName) ? FsResult.Success(FileInfo()) : FsResult.Error(NtStatus.ObjectNameNotFound));

    public ValueTask<int> CanDelete(string f, FileOperationInfo i, CancellationToken ct) => new(NtStatus.AccessDenied);

    public unsafe ValueTask<ReadDirectoryResult> ReadDirectory(string fileName, string? pattern, string? marker,
        nint buffer, uint length, FileOperationInfo info, CancellationToken ct)
    {
        uint bt = 0;
        var names = new[] { "video.bin", "other.bin" };
        bool passed = string.IsNullOrEmpty(marker);
        foreach (var name in names)
        {
            if (!passed) { if (string.Equals(name, marker, StringComparison.OrdinalIgnoreCase)) passed = true; continue; }
            var di = new FspDirInfo(); di.FileInfo = FileInfo(); di.SetFileName(name);
            if (!WinFspFileSystem.AddDirInfo(&di, buffer, length, &bt)) return new(ReadDirectoryResult.Success(bt));
        }
        WinFspFileSystem.EndDirInfo(buffer, length, &bt);
        return new(ReadDirectoryResult.Success(bt));
    }

    private bool IsFile(string f) =>
        string.Equals(f, "\\video.bin", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(f, "\\other.bin", StringComparison.OrdinalIgnoreCase);

    private FspFileInfo FileInfo()
    {
        ulong now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        return new FspFileInfo { FileAttributes = (uint)FileAttributes.Archive, FileSize = (ulong)_size,
            AllocationSize = (ulong)_size, CreationTime = now, LastAccessTime = now, LastWriteTime = now, ChangeTime = now, IndexNumber = 1 };
    }
    private FspFileInfo Dir()
    {
        ulong now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        return new FspFileInfo { FileAttributes = (uint)FileAttributes.Directory, CreationTime = now, LastAccessTime = now, LastWriteTime = now, ChangeTime = now };
    }
}
