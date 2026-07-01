using System.Diagnostics;
using System.Runtime.Versioning;
using WinFsp.Native;

namespace SlowReadRepro;

[SupportedOSPlatform("windows")]
internal static class Program
{
    static int Main(string[] args)
    {
        Console.WriteLine("=== WinFsp slow-read isolation repro ===");
        Console.WriteLine($"CPU cores: {Environment.ProcessorCount}  (WinFsp default dispatcher threads ≈ this, clamped 4..16)");
        Console.WriteLine();

        // Sub-command: same-file read serialization experiment.
        if (args.Length > 0 && string.Equals(args[0], "serialize", StringComparison.OrdinalIgnoreCase))
        {
            SerializeExperiment.Run(GetInt(args, "threadCount", 0));
            return 0;
        }

        // Sub-command: D4 — partial-return + consumer re-read; does a same-file OPEN slip through?
        if (args.Length > 0 && string.Equals(args[0], "d4", StringComparison.OrdinalIgnoreCase))
        {
            RepeatReadExperiment.Run(
                budgetMs: GetInt(args, "budgetMs", 800),
                threadCountArg: GetInt(args, "threadCount", 0),
                probeSeconds: GetInt(args, "probeSeconds", 6),
                slowThreads: GetInt(args, "slowThreads", 1));
            return 0;
        }

        // Parameters tuned to mirror the ZipDrive repro:
        //  - several concurrent slow reads (videos extracting), each ~2s
        //  - many fast reads (image thumbnails) that should stay snappy
        int fastCount = 64;
        int slowConcurrency = GetInt(args, "slowConcurrency", 8); // 8 videos like the Dokan log
        int slowDelayMs = GetInt(args, "slowDelayMs", 2000);
        int fastProbeMs = GetInt(args, "fastProbeMs", 4000);     // how long to hammer fast reads
        int threadCount = GetInt(args, "threadCount", 0);        // 0 = WinFsp default (= cores, clamp 4..16)

        // Run all three modes unless one is named.
        SlowFs.SlowMode[] modes = args.Length > 0 && Enum.TryParse<SlowFs.SlowMode>(args[0], true, out var only)
            ? [only]
            : [SlowFs.SlowMode.AsyncDelay, SlowFs.SlowMode.ThreadSleep, SlowFs.SlowMode.SyncOverAsync];

        foreach (var mode in modes)
        {
            RunScenario(mode, fastCount, slowConcurrency, slowDelayMs, fastProbeMs, threadCount);
        }

        Console.WriteLine();
        Console.WriteLine("Interpretation:");
        Console.WriteLine("  If fast-read p99/max stays low (~ms) while slow reads run => slow reads are ISOLATED (good, Dokan-like).");
        Console.WriteLine("  If fast-read p99/max balloons toward slowDelayMs while slow reads run => slow reads BLOCK the volume (the hang).");
        return 0;
    }

    [SupportedOSPlatform("windows")]
    static void RunScenario(SlowFs.SlowMode mode, int fastCount, int slowConcurrency, int slowDelayMs, int fastProbeMs, int threadCount)
    {
        Console.WriteLine($"────────────────────────────────────────────────────────");
        Console.WriteLine($"MODE = {mode}  (slowConcurrency={slowConcurrency}, slowDelay={slowDelayMs}ms, probe={fastProbeMs}ms, threadCount={(threadCount == 0 ? "default" : threadCount.ToString())})");

        var fs = new SlowFs(fastCount, slowDelayMs, mode);
        var host = new FileSystemHost(fs)
        {
            Prefix = $@"\winfsp-slowrepro\{mode}-{Environment.ProcessId}"
        };

        int mr = host.MountEx(null, (uint)threadCount);
        if (mr < 0)
        {
            Console.WriteLine($"  MOUNT FAILED: 0x{mr:X8}. Is WinFsp installed?");
            return;
        }

        string root = host.MountPoint!;
        if (!root.EndsWith('\\')) root += "\\";
        Console.WriteLine($"  mounted at UNC: {root}");

        try
        {
            using var cts = new CancellationTokenSource();

            // Baseline: measure fast-read latency with NO slow reads in flight.
            var baseline = MeasureFastReads(root, fastCount, durationMs: 800, CancellationToken.None);
            Console.WriteLine($"  [baseline, no slow reads]   {baseline}");

            // Start N concurrent slow reads looping (simulating videos extracting).
            var slowTasks = new Task[slowConcurrency];
            for (int i = 0; i < slowConcurrency; i++)
            {
                slowTasks[i] = Task.Run(() =>
                {
                    var buf = new byte[64 * 1024];
                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            using var h = File.OpenHandle(root + "slow.bin", FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite, FileOptions.None);
                            RandomAccess.Read(h, buf, 0);
                        }
                        catch { /* ignore during teardown */ }
                    }
                });
            }

            // While slow reads are looping, hammer fast reads and measure latency.
            var underLoad = MeasureFastReads(root, fastCount, fastProbeMs, CancellationToken.None);
            Console.WriteLine($"  [under {slowConcurrency} slow reads]   {underLoad}");

            cts.Cancel();
            Task.WaitAll(slowTasks, 5000);

            // verdict line
            bool blocked = underLoad.P99Ms > Math.Max(200, baseline.P99Ms * 20);
            Console.WriteLine($"  => fast reads {(blocked ? "BLOCKED by slow reads (reproduces hang)" : "ISOLATED from slow reads (no hang)")}");
        }
        finally
        {
            host.Dispose();
        }
        Console.WriteLine();
    }

    static LatencyStats MeasureFastReads(string root, int fastCount, int durationMs, CancellationToken ct)
    {
        var latencies = new List<double>(4096);
        var lockObj = new object();
        int parallelism = Environment.ProcessorCount * 2;
        var sw = Stopwatch.StartNew();
        var buf = new ThreadLocal<byte[]>(() => new byte[64 * 1024]);

        Parallel.For(0, parallelism, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, _ =>
        {
            var rnd = new Random(Environment.CurrentManagedThreadId);
            var local = new List<double>(1024);
            var b = buf.Value!;
            while (sw.ElapsedMilliseconds < durationMs && !ct.IsCancellationRequested)
            {
                int idx = rnd.Next(fastCount);
                var t0 = Stopwatch.GetTimestamp();
                try
                {
                    using var h = File.OpenHandle(root + $"fast-{idx}.bin", FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, FileOptions.None);
                    RandomAccess.Read(h, b, 0);
                }
                catch { continue; }
                local.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            lock (lockObj) latencies.AddRange(local);
        });

        return LatencyStats.From(latencies);
    }

    static int GetInt(string[] args, string key, int dflt)
    {
        foreach (var a in args)
            if (a.StartsWith($"--{key}=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(a.AsSpan($"--{key}=".Length), out var v)) return v;
        return dflt;
    }
}

internal readonly record struct LatencyStats(int Count, double P50Ms, double P99Ms, double MaxMs)
{
    public static LatencyStats From(List<double> xs)
    {
        if (xs.Count == 0) return new(0, 0, 0, 0);
        xs.Sort();
        double P(double q) => xs[(int)Math.Clamp(q * xs.Count, 0, xs.Count - 1)];
        return new(xs.Count, P(0.50), P(0.99), xs[^1]);
    }

    public override string ToString()
        => $"n={Count,5}  p50={P50Ms,7:F2}ms  p99={P99Ms,8:F2}ms  max={MaxMs,8:F2}ms";
}
