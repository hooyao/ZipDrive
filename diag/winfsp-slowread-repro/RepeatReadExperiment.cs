using System.Diagnostics;
using System.Runtime.Versioning;
using WinFsp.Native;

namespace SlowReadRepro;

/// <summary>
/// Experiment D4 (v2, cleaned up) — the decisive go/no-go test for the P0 "partial return" fix.
///
/// Core question: while a file has slow reads in flight that hold WinFsp's FileNode Main SHARED
/// lock, can a CreateFile (OPEN) to the SAME file — which needs Main EXCLUSIVE on its completion
/// side (winfsp create.c:1326, TryAcquireExclusive + re-queue) — get through?
///
/// Two shared-lock models:
///   • block  : ONE slow read holds shared for the whole extraction (models the CURRENT infinite
///              EnsureChunkReadyAsync wait). Expect OPEN blocked for the full duration.
///   • partial: slow reads each return after a BUDGET with a partial, so the consumer re-issues —
///              shared is acquired for budgetMs, released, re-acquired, repeatedly (models the FIX).
///              The go/no-go question is whether OPEN's exclusive slips into a release gap.
///
/// v2 fixes over v1:
///   • Measures OPEN latency ALONE (open then immediately close — no read) so we isolate the
///     exclusive-acquire wait from any read queuing.
///   • Also measures a head READ latency separately, for context.
///   • Single probe thread (no probe-vs-probe contention).
///   • A BASELINE phase with no slow activity, to show what a normal open costs on this volume.
///   • The slow side is a dedicated loop re-reading a FIXED tail offset, so the shared-lock
///     pressure is steady for the whole probe window (v1's consumer changed offsets and finished).
///
/// Run:  SlowReadRepro d4 [--budgetMs=800] [--threadCount=N] [--probeSeconds=6] [--slowThreads=1]
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RepeatReadExperiment
{
    const long FileSize = 64L * 1024 * 1024;
    const long TailStart = 32L * 1024 * 1024;
    const long FixedTailOffset = 48L * 1024 * 1024; // slow loop always reads here (deep in the tail)

    public static void Run(int budgetMs, int threadCountArg, int probeSeconds, int slowThreads)
    {
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine("EXPERIMENT D4 (v2): can a same-file OPEN get through while slow reads hold the shared lock?");
        Console.WriteLine($"  budgetMs={budgetMs}  threadCount={(threadCountArg == 0 ? "default" : threadCountArg.ToString())}  probeSeconds={probeSeconds}  slowThreads={slowThreads}");
        Console.WriteLine($"  file={FileSize / (1024 * 1024)}MB  tailStart={TailStart / (1024 * 1024)}MB  slowLoopOffset={FixedTailOffset / (1024 * 1024)}MB");
        Console.WriteLine("  OPEN latency is measured ALONE (open+close, no read) to isolate the exclusive-acquire wait.");

        RunOnce("BASELINE: no slow reads (normal open cost)", budgetMs, SlowKind.None, threadCountArg, probeSeconds, slowThreads);
        RunOnce("CONTROL: one long blocking tail read (current behavior)", budgetMs, SlowKind.Block, threadCountArg, probeSeconds, slowThreads);
        RunOnce("D4: partial-return, slow reads re-issued at budget cadence (post-fix)", budgetMs, SlowKind.Partial, threadCountArg, probeSeconds, slowThreads);
    }

    enum SlowKind { None, Block, Partial }

    static void RunOnce(string title, int budgetMs, SlowKind kind, int threadCountArg, int probeSeconds, int slowThreads)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} ──");

        var fs = new RepeatSlowFs(FileSize, TailStart, budgetMs, kind == SlowKind.Partial);
        var host = new FileSystemHost(fs) { Prefix = $@"\winfsp-d4\{kind}-{Environment.ProcessId}" };
        int mr = host.MountEx(null, (uint)threadCountArg);
        if (mr < 0) { Console.WriteLine($"  MOUNT FAILED 0x{mr:X8}"); return; }
        string root = host.MountPoint!;
        if (!root.EndsWith('\\')) root += "\\";

        using var stop = new CancellationTokenSource();
        long slowReadsIssued = 0;
        var slowTasks = new List<Task>();
        try
        {
            if (kind != SlowKind.None)
            {
                var slowStarted = new ManualResetEventSlim();
                for (int i = 0; i < slowThreads; i++)
                {
                    slowTasks.Add(Task.Run(() =>
                    {
                        var buf = new byte[64 * 1024];
                        slowStarted.Set();
                        while (!stop.IsCancellationRequested)
                        {
                            try
                            {
                                // Fresh handle each iteration; read the FIXED deep-tail offset.
                                // block  => this single read blocks for the whole (capped) extraction.
                                // partial=> returns after budgetMs with a partial; loop re-issues =>
                                //           steady shared acquire/release/re-acquire pressure.
                                using var h = File.OpenHandle(root + "video.bin", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.None);
                                RandomAccess.Read(h, buf, FixedTailOffset);
                                Interlocked.Increment(ref slowReadsIssued);
                            }
                            catch { }
                        }
                    }, stop.Token));
                }
                slowStarted.Wait();
                Thread.Sleep(200); // let slow reads reach the FS and start holding the lock
            }

            // ── Probe: measure OPEN-alone latency and (separately) head-READ latency. ──
            var openLat = new List<double>(4096);
            var readLat = new List<double>(4096);
            var probeSw = Stopwatch.StartNew();
            var pbuf = new byte[4096];
            while (probeSw.ElapsedMilliseconds < probeSeconds * 1000 && !stop.IsCancellationRequested)
            {
                // OPEN alone — open then close immediately, NO read. This is the operation whose
                // completion side needs Main EXCLUSIVE.
                var o0 = Stopwatch.GetTimestamp();
                try
                {
                    using var h = File.OpenHandle(root + "video.bin", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.None);
                }
                catch { }
                openLat.Add(Stopwatch.GetElapsedTime(o0).TotalMilliseconds);

                // Head READ (fast region) for context — open+read on a fresh handle.
                var r0 = Stopwatch.GetTimestamp();
                try
                {
                    using var h = File.OpenHandle(root + "video.bin", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.None);
                    RandomAccess.Read(h, pbuf, 0);
                }
                catch { }
                readLat.Add(Stopwatch.GetElapsedTime(r0).TotalMilliseconds);

                Thread.Sleep(50);
            }

            stop.Cancel();
            if (slowTasks.Count > 0) Task.WaitAll(slowTasks.ToArray(), 5000);

            var so = LatencyStats.From(openLat);
            var sr = LatencyStats.From(readLat);
            Console.WriteLine($"  OPEN-alone (needs exclusive)  : {so}");
            Console.WriteLine($"  head READ (open+read, ctx)    : {sr}");
            if (kind != SlowKind.None)
                Console.WriteLine($"  slow reads issued during probe: {Interlocked.Read(ref slowReadsIssued)}");

            if (kind == SlowKind.Partial)
            {
                bool starved = so.P99Ms > 1500;
                Console.WriteLine($"  => same-file OPEN {(starved ? "STARVED (fix does NOT rescue open — NO-GO)" : "slips through between shared holds (fix works — GO)")}  [threshold p99>1500ms]");
            }
        }
        finally
        {
            host.Dispose();
        }
    }
}

/// <summary>
/// FS with one big video.bin. Reads before <see cref="_tailStart"/> are instant. Reads at/after it:
///   • partial=false: block for the whole (capped) sequential extraction — one long shared hold.
///   • partial=true : wait a per-read BUDGET, then return a PARTIAL sized by how far a modeled
///     sequential extraction has progressed since mount. Never returns 0 (that would be EOF).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RepeatSlowFs : IFileSystem
{
    private readonly long _size;
    private readonly long _tailStart;
    private readonly int _budgetMs;
    private readonly bool _partial;
    private readonly byte[] _content;
    private readonly long _mountTicks;

    const double ExtractBytesPerSec = 8.0 * 1024 * 1024;

    public RepeatSlowFs(long size, long tailStart, int budgetMs, bool partial)
    {
        _size = size; _tailStart = tailStart; _budgetMs = budgetMs; _partial = partial;
        _content = new byte[64 * 1024];
        Array.Fill(_content, (byte)0xCD);
        _mountTicks = Stopwatch.GetTimestamp();
    }

    public bool SynchronousIo => false;

    public int Init(FileSystemHost host)
    {
        host.SectorSize = 4096; host.SectorsPerAllocationUnit = 1; host.MaxComponentLength = 255;
        host.FileInfoTimeout = 0; host.CasePreservedNames = true; host.UnicodeOnDisk = true;
        host.VolumeSerialNumber = 0x44344422; host.FileSystemName = "NTFS";
        return NtStatus.Success;
    }

    public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
    { totalSize = 1UL << 31; freeSize = 0; volumeLabel = "D4"; return NtStatus.Success; }

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
        bool isTail = offset >= (ulong)_tailStart;

        if (isVideo && isTail)
        {
            if (_partial)
            {
                await Task.Delay(_budgetMs, ct).ConfigureAwait(false);

                double elapsedSec = Stopwatch.GetElapsedTime(_mountTicks).TotalSeconds;
                long readyBytes = _tailStart + (long)(elapsedSec * ExtractBytesPerSec);
                long availableFromOffset = readyBytes - (long)offset;

                int cap = buffer.Length;
                int give = availableFromOffset <= 0
                    ? Math.Min(4096, cap)
                    : (int)Math.Min((long)cap, availableFromOffset);

                FillFrom(buffer, give);
                return ReadResult.Success((uint)give);
            }
            else
            {
                double fullSec = (_size - _tailStart) / ExtractBytesPerSec;
                int blockMs = (int)Math.Min(fullSec * 1000, 8000);
                await Task.Delay(blockMs, ct).ConfigureAwait(false);
            }
        }

        int n = (int)Math.Min((long)buffer.Length, _size - (long)offset);
        FillFrom(buffer, n);
        return ReadResult.Success((uint)n);
    }

    private void FillFrom(Memory<byte> buffer, int count)
    {
        int filled = 0;
        while (filled < count)
        {
            int chunk = Math.Min(_content.Length, count - filled);
            _content.AsSpan(0, chunk).CopyTo(buffer.Span.Slice(filled, chunk));
            filled += chunk;
        }
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
        var di = new FspDirInfo(); di.FileInfo = FileInfo(); di.SetFileName("video.bin");
        if (!WinFspFileSystem.AddDirInfo(&di, buffer, length, &bt)) return new(ReadDirectoryResult.Success(bt));
        WinFspFileSystem.EndDirInfo(buffer, length, &bt);
        return new(ReadDirectoryResult.Success(bt));
    }

    private bool IsFile(string f) => string.Equals(f, "\\video.bin", StringComparison.OrdinalIgnoreCase);

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
