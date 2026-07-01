using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// probe: measures whether a slow TAIL read of video.bin serializes/blocks a
// concurrent OPEN+HEAD read of (a) the SAME file and (b) a DIFFERENT file.
//
// Usage: probe.dll <root>   e.g.  probe.dll \\creproC\share
// Reports three numbers: tail read ms, same-file open+head ms, other-file open+head ms.
class P
{
    const long FileSize = 64L * 1024 * 1024;
    const int Head = 64 * 1024;

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : @"\\winfsp-crepro\share";
        if (!root.EndsWith("\\")) root += "\\";
        string video = root + "video.bin";
        string other = root + "other.bin";

        Console.WriteLine($"probe root = {root}");

        // Kick off ONE slow tail read of video.bin.
        var started = new ManualResetEventSlim();
        double slowMs = -1;
        string slowErr = null;
        var slow = Task.Run(() =>
        {
            try
            {
                using var h = File.OpenHandle(video, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[Head];
                started.Set();
                var t0 = Stopwatch.GetTimestamp();
                RandomAccess.Read(h, buf, FileSize - buf.Length);
                slowMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            catch (Exception ex) { slowErr = ex.Message; started.Set(); }
        });

        started.Wait();
        Thread.Sleep(200); // ensure the tail read is in-flight inside the FS before we probe

        // Probe A: concurrent OPEN + HEAD read of the SAME file (video.bin, offset 0 = instant region).
        // Time OPEN and READ separately so we can see WHICH phase serializes.
        double sameOpen = MeasureOpenThenRead(video, 0, out double sameRead, out string sameErr);

        // Probe B: concurrent OPEN + HEAD read of a DIFFERENT file (other.bin, always instant).
        double otherOpen = MeasureOpenThenRead(other, 0, out double otherRead, out string otherErr);

        slow.Wait(15000);

        Console.WriteLine($"tail read (video.bin tail, slow)     : {Fmt(slowMs, slowErr)}");
        Console.WriteLine($"SAME-file  OPEN (video.bin)          : {Fmt(sameOpen, sameErr)}  {Verdict(sameOpen)}");
        Console.WriteLine($"SAME-file  HEAD read (video.bin @0)  : {Fmt(sameRead, sameErr)}  {Verdict(sameRead)}");
        Console.WriteLine($"OTHER-file OPEN (other.bin)          : {Fmt(otherOpen, otherErr)}  {Verdict(otherOpen)}");
        Console.WriteLine($"OTHER-file HEAD read (other.bin @0)  : {Fmt(otherRead, otherErr)}  {Verdict(otherRead)}");
        return 0;
    }

    // Open the handle (phase 1), then read the head (phase 2), timing each separately.
    static double MeasureOpenThenRead(string path, long offset, out double readMs, out string err)
    {
        err = null;
        readMs = -1;
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
        catch (Exception ex)
        {
            err = ex.Message;
            return Stopwatch.GetElapsedTime(tOpen).TotalMilliseconds;
        }
    }

    static string Fmt(double ms, string err) => err != null ? $"ERR: {err}" : $"{ms,8:F1}ms";
    static string Verdict(double ms) => ms < 0 ? "" : (ms > 500 ? "<== BLOCKED (serialized)" : "OK (not blocked)");
}
