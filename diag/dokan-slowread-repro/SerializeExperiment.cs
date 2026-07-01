using System.Diagnostics;
using System.Runtime.Versioning;
using DokanNet;
using DokanNet.Logging;
using FileAccess = System.IO.FileAccess; // this file only does real System.IO handle reads

namespace DokanSlowReadRepro;

/// <summary>
/// Dokan counterpart of the WinFsp <c>SerializeExperiment</c>. Answers the open question in
/// diag/HANDOVER.md: on Dokan, does a slow tail read of \video.bin block a CONCURRENT open+read
/// of the SAME file (a second handle)?  WinFsp blocks it (~2850 ms); the code analysis in
/// diag/out/DOKAN-VS-WINFSP-LOCK-ANALYSIS.md predicts Dokan does NOT (it releases the FCB lock
/// before pending the read).
///
/// SAFETY: mounts to a fresh empty temp DIRECTORY (never a drive letter), write-protected, and
/// disposes deterministically. A directory mount cannot leave a zombie drive letter behind.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SerializeExperiment
{
    const long FileSize = 8L * 1024 * 1024; // 8 MB, so a tail read is meaningfully far in

    public static void Run(int threadCount, bool debug)
    {
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine("EXPERIMENT: same-file read serialization on DOKAN");
        Console.WriteLine($"  CPU cores = {Environment.ProcessorCount}");
        Console.WriteLine($"  threadCount = {(threadCount == 0 ? "default (single-thread=false)" : threadCount.ToString())}");
        Console.WriteLine("  (SerializeExperiment issues ONE slow read at a time; >=2 dispatcher");
        Console.WriteLine("   threads rules out dispatcher-thread exhaustion as the cause.)");

        RunOnce("BLOCKING tail read (current ZipDrive behavior)", slowTailDelayMs: 3000, partialFallback: false, threadCount, debug);
        RunOnce("PARTIAL-RETURN tail read (proposed P0 fix)",     slowTailDelayMs: 3000, partialFallback: true,  threadCount, debug);
    }

    static void RunOnce(string title, int slowTailDelayMs, bool partialFallback, int threadCount, bool debug)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} ──");

        string mountDir = Path.Combine(Path.GetTempPath(), $"dokan-serialize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountDir);

        var fs = new TailSlowFs(FileSize, slowTailDelayMs, partialFallback);
        ILogger dokanLogger = debug ? new ConsoleLogger("[Dokan] ") : new NullLogger();
        using var dokan = new Dokan(dokanLogger);

        DokanInstance? instance = null;
        try
        {
            var builder = new DokanInstanceBuilder(dokan)
                .ConfigureOptions(options =>
                {
                    options.Options = DokanOptions.WriteProtection | (debug ? DokanOptions.DebugMode | DokanOptions.StderrOutput : 0);
                    options.MountPoint = mountDir;      // directory mount, NOT a drive letter
                    options.SingleThread = threadCount == 1;
                });
            instance = builder.Build(fs);

            // Wait for the mount to become usable.
            if (!WaitForMount(mountDir, TimeSpan.FromSeconds(15)))
            {
                Console.WriteLine("  MOUNT FAILED (timed out waiting for volume). Is the Dokany 2.x driver installed?");
                return;
            }

            string root = mountDir.EndsWith('\\') ? mountDir : mountDir + "\\";

            // Kick off ONE tail read of video.bin (the "Photos reads moov" read).
            var slowStarted = new ManualResetEventSlim();
            var slowTask = Task.Run(() =>
            {
                using var h = File.OpenHandle(root + "video.bin", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[64 * 1024];
                slowStarted.Set();
                var t0 = Stopwatch.GetTimestamp();
                RandomAccess.Read(h, buf, FileSize - buf.Length); // tail => slow
                return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            });

            slowStarted.Wait();
            Thread.Sleep(150); // let the slow read reach the FS callback

            double sameFileHead = TimeRead(root + "video.bin", offset: 0);   // second handle, head => should be fast
            double slowMs = slowTask.GetAwaiter().GetResult();

            Console.WriteLine($"  tail read (video.bin)            : {slowMs,8:F1}ms");
            Console.WriteLine($"  concurrent HEAD SAME file (sync) : {sameFileHead,8:F1}ms  " +
                              $"{(sameFileHead > 500 ? "<== BLOCKED (same as WinFsp — hypothesis WRONG)" : "OK (NOT blocked — matches Dokan prediction)")}");
        }
        finally
        {
            instance?.Dispose(); // DokanCloseHandle + wait for dismount
            TryDeleteDir(mountDir);
        }
    }

    static double TimeRead(string path, long offset)
    {
        var buf = new byte[64 * 1024];
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.None);
            RandomAccess.Read(h, buf, offset);
        }
        catch { }
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    static bool WaitForMount(string mountDir, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        string probe = Path.Combine(mountDir, "video.bin");
        while (sw.Elapsed < timeout)
        {
            try { if (File.Exists(probe)) return true; }
            catch { }
            Thread.Sleep(100);
        }
        return false;
    }

    static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
