using System.Collections.Concurrent;
using System.Text;
using Xunit.Abstractions;

namespace ZipDrive.EnduranceTests;

/// <summary>
/// Thread-safe latency recorder with reservoir sampling to bound memory.
/// Records elapsed times by category and computes percentile reports.
/// </summary>
public sealed class LatencyRecorder
{
    private const int MaxSamplesPerCategory = 100_000;
    private readonly ConcurrentDictionary<string, ReservoirSampler> _samplers = new();

    public void Record(string category, double elapsedMs)
    {
        var sampler = _samplers.GetOrAdd(category, _ => new ReservoirSampler(MaxSamplesPerCategory));
        sampler.Add(elapsedMs);
    }

    public Dictionary<string, LatencyStats> ComputeAll()
    {
        Dictionary<string, LatencyStats> result = new();
        foreach (var (category, sampler) in _samplers)
        {
            result[category] = sampler.ComputeStats();
        }
        return result;
    }

    public void PrintReport(ITestOutputHelper output)
    {
        var stats = ComputeAll();
        if (stats.Count == 0) return;

        output.WriteLine("");
        output.WriteLine("═══════════════════════════════════════════════════════════════");
        output.WriteLine("  Latency Report");
        output.WriteLine("═══════════════════════════════════════════════════════════════");
        output.WriteLine("");
        output.WriteLine($"  {"Category",-25} {"Samples",8}  {"p50",8}  {"p95",8}  {"p99",8}  {"max",8}");
        output.WriteLine($"  {"─────────────────────────",-25} {"────────",8}  {"────────",8}  {"────────",8}  {"────────",8}  {"────────",8}");

        foreach (var (category, s) in stats.OrderBy(kv => kv.Key))
        {
            output.WriteLine($"  {category,-25} {s.SampleCount,8}  {s.P50Ms,7:F2}ms  {s.P95Ms,7:F2}ms  {s.P99Ms,7:F2}ms  {s.MaxMs,7:F2}ms");
        }

        output.WriteLine("");
        output.WriteLine("  Design Targets (informational):");
        output.WriteLine("    Cache hit < 1ms overhead");
        output.WriteLine("    Cache miss (small) < 100ms");
        output.WriteLine("    Cache miss (large, 1st byte) ~50ms");
        output.WriteLine("");
        output.WriteLine("═══════════════════════════════════════════════════════════════");
    }

    private sealed class ReservoirSampler
    {
        private readonly double[] _reservoir;
        private readonly int _capacity;
        private long _count;
        private readonly object _lock = new();

        public ReservoirSampler(int capacity)
        {
            _capacity = capacity;
            _reservoir = new double[capacity];
        }

        public void Add(double value)
        {
            lock (_lock)
            {
                long n = _count++;
                if (n < _capacity)
                {
                    _reservoir[n] = value;
                }
                else
                {
                    // Reservoir sampling: replace with probability capacity/n
                    long j = Random.Shared.NextInt64(n + 1);
                    if (j < _capacity)
                        _reservoir[j] = value;
                }
            }
        }

        public LatencyStats ComputeStats()
        {
            lock (_lock)
            {
                int filled = (int)Math.Min(_count, _capacity);
                if (filled == 0)
                    return new LatencyStats(0, 0, 0, 0, 0);

                double[] sorted = new double[filled];
                Array.Copy(_reservoir, sorted, filled);
                Array.Sort(sorted);

                return new LatencyStats(
                    SampleCount: _count,
                    P50Ms: Percentile(sorted, 0.50),
                    P95Ms: Percentile(sorted, 0.95),
                    P99Ms: Percentile(sorted, 0.99),
                    MaxMs: sorted[^1]);
            }
        }

        private static double Percentile(double[] sorted, double p)
        {
            double index = p * (sorted.Length - 1);
            int lower = (int)Math.Floor(index);
            int upper = (int)Math.Ceiling(index);
            if (lower == upper || upper >= sorted.Length) return sorted[lower];
            double frac = index - lower;
            return sorted[lower] * (1 - frac) + sorted[upper] * frac;
        }
    }
}

public readonly record struct LatencyStats(
    long SampleCount,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs);
