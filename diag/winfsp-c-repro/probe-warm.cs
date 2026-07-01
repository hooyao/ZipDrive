using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// probe-warm: warm-kernel-cache experiments.
//
// Modes (arg[1]):
//   A <root>            scenario A: read video.bin HEAD (offset 0) TWICE with a gap.
//                       Reports both durations. (Ground truth for "did the 2nd read
//                       dispatch a user-mode Read callback" comes from the repro's
//                       --debug stderr: count 'Read ENTER video.bin offset=0' lines.)
//   B <root>            scenario B (core): warm video.bin metadata (open+GetInfo+close),
//                       then start a slow TAIL read, then concurrently OPEN+HEAD the
//                       SAME file and OPEN the OTHER file. Split open/read timing.
//
// Usage: probe-warm.dll <A|B> <root>
class PW
{
    const long FileSize = 64L * 1024 * 1024;
    const int Head = 64 * 1024;

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToUpperInvariant() : "B";
        string root = args.Length > 1 ? args[1] : @"\\winfsp-crepro\share";
        // Optional per-run tail offset (scenario B). MUST be unique per run under infinite
        // cache, else the tail read hits the cache from a previous run and is not a miss.
        long tailOffset = args.Length > 2 && long.TryParse(args[2], out var to) ? to : (FileSize - Head);
        if (!root.EndsWith("\\")) root += "\\";
        string video = root + "video.bin";
        string other = root + "other.bin";
        Console.WriteLine($"probe-warm mode={mode} root={root} tailOffset={tailOffset}");

        if (mode == "A") return ScenarioA(video);
        return ScenarioB(video, other, tailOffset);
    }

    // ── Scenario A: same-offset HEAD read twice ──
    static int ScenarioA(string video)
    {
        double r1 = ReadAt(video, 0, out string e1);
        Console.WriteLine($"HEAD read #1 (cold, offset 0)         : {Fmt(r1, e1)}");
        Thread.Sleep(300);
        double r2 = ReadAt(video, 0, out string e2);
        Console.WriteLine($"HEAD read #2 (warm?, offset 0)        : {Fmt(r2, e2)}");
        Console.WriteLine("NOTE: whether read#2 hit kernel cache is proven by the repro --debug log:");
        Console.WriteLine("      count 'Read ENTER video.bin offset=0' callbacks (expect 1 if cache works).");
        return 0;
    }

    // ── Scenario B: warm metadata, slow tail read in-flight, probe same+other open ──
    static int ScenarioB(string video, string other, long tailOffset)
    {
        // 1) Warm video.bin metadata: open, GetFileInfo (via handle), close. No tail read,
        //    so this open is fast and populates FileInfo/Security in kernel cache.
        var tw = Stopwatch.GetTimestamp();
        try
        {
            using var hw = File.OpenHandle(video, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _ = RandomAccess.GetLength(hw);
        }
        catch (Exception ex) { Console.WriteLine($"WARM open ERR: {ex.Message}"); }
        Console.WriteLine($"warm-up open+getinfo (video.bin)     : {Stopwatch.GetElapsedTime(tw).TotalMilliseconds,8:F1}ms");
        Thread.Sleep(200);

        // 2) Start ONE slow TAIL read at a UNIQUE offset (cache miss -> dispatches
        //    user-mode Read, holds Main shared across the 3s user-mode round trip).
        var started = new ManualResetEventSlim();
        double slowMs = -1; string slowErr = null;
        var slow = Task.Run(() =>
        {
            try
            {
                using var h = File.OpenHandle(video, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[Head];
                started.Set();
                var t0 = Stopwatch.GetTimestamp();
                RandomAccess.Read(h, buf, tailOffset);
                slowMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            catch (Exception ex) { slowErr = ex.Message; started.Set(); }
        });
        started.Wait();
        Thread.Sleep(200); // ensure tail read is in-flight inside the FS

        // 3) Concurrent OPEN+HEAD of SAME file, and OPEN of OTHER file. Split timing.
        double sameOpen = OpenThenRead(video, 0, out double sameRead, out string sameErr);
        double otherOpen = OpenThenRead(other, 0, out double otherRead, out string otherErr);

        slow.Wait(15000);

        Console.WriteLine($"tail read (video.bin tail, slow)     : {Fmt(slowMs, slowErr)}");
        Console.WriteLine($"SAME-file  OPEN (video.bin, warm md) : {Fmt(sameOpen, sameErr)}  {Verdict(sameOpen)}");
        Console.WriteLine($"SAME-file  HEAD read (video.bin @0)  : {Fmt(sameRead, sameErr)}  {Verdict(sameRead)}");
        Console.WriteLine($"OTHER-file OPEN (other.bin)          : {Fmt(otherOpen, otherErr)}  {Verdict(otherOpen)}");
        Console.WriteLine($"OTHER-file HEAD read (other.bin @0)  : {Fmt(otherRead, otherErr)}  {Verdict(otherRead)}");
        return 0;
    }

    static double ReadAt(string path, long offset, out string err)
    {
        err = null;
        var t = Stopwatch.GetTimestamp();
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var b = new byte[Head];
            RandomAccess.Read(h, b, offset);
        }
        catch (Exception ex) { err = ex.Message; }
        return Stopwatch.GetElapsedTime(t).TotalMilliseconds;
    }

    static double OpenThenRead(string path, long offset, out double readMs, out string err)
    {
        err = null; readMs = -1;
        var tOpen = Stopwatch.GetTimestamp();
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            double openMs = Stopwatch.GetElapsedTime(tOpen).TotalMilliseconds;
            var b = new byte[Head];
            var tRead = Stopwatch.GetTimestamp();
            RandomAccess.Read(h, b, offset);
            readMs = Stopwatch.GetElapsedTime(tRead).TotalMilliseconds;
            return openMs;
        }
        catch (Exception ex) { err = ex.Message; return Stopwatch.GetElapsedTime(tOpen).TotalMilliseconds; }
    }

    static string Fmt(double ms, string err) => err != null ? $"ERR: {err}" : $"{ms,8:F1}ms";
    static string Verdict(double ms) => ms < 0 ? "" : (ms > 500 ? "<== BLOCKED (serialized)" : "OK (not blocked)");
}
