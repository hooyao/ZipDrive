using System.Runtime.Versioning;

#if NET5_0_OR_GREATER
[assembly: SupportedOSPlatform("windows")]
#endif

namespace DokanSlowReadRepro;

internal static class Program
{
    static int Main(string[] args)
    {
        Console.WriteLine("=== Dokan slow-read serialization repro (mirror of WinFsp SerializeExperiment) ===");
        Console.WriteLine();

        int threadCount = GetInt(args, "threadCount", 0); // 0 = Dokan default multi-thread; 1 = SingleThread
        bool debug = HasFlag(args, "debug");

        try
        {
            SerializeExperiment.Run(threadCount, debug);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("Interpretation:");
        Console.WriteLine("  If HEAD-same-file stays low (~ms) while the tail read blocks => Dokan does NOT serialize");
        Console.WriteLine("     same-file reads => confirms the WinFsp-only lock-hold hypothesis.");
        Console.WriteLine("  If HEAD-same-file balloons toward the tail delay => Dokan ALSO blocks => hypothesis wrong,");
        Console.WriteLine("     re-open the diagnosis.");
        Console.WriteLine("  Cross-check with --threadCount=4 and --threadCount=1 to separate FCB-lock from thread starvation.");
        return 0;
    }

    static int GetInt(string[] args, string key, int dflt)
    {
        foreach (var a in args)
            if (a.StartsWith($"--{key}=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(a.AsSpan($"--{key}=".Length), out var v)) return v;
        return dflt;
    }

    static bool HasFlag(string[] args, string key)
    {
        foreach (var a in args)
            if (string.Equals(a, $"--{key}", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
