using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit.Abstractions;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Infrastructure.FileSystem;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests.Suites;

/// <summary>
/// Dedicated latency measurement: cache hit/miss classification,
/// linear vs random access categorization.
/// </summary>
public sealed class LatencyMeasurementSuite : EnduranceSuiteBase
{
    private readonly LatencyRecorder _recorder;
    private readonly ConcurrentDictionary<string, bool> _seenFiles = new();

    public override string Name => "LatencyMeasurementSuite";
    public override int TaskCount => 5;

    public LatencyMeasurementSuite(
        DokanFileSystemAdapter adapter,
        ConcurrentDictionary<string, ZipManifest> manifests,
        List<string> archivePaths,
        FileContentCache fileCache,
        IArchiveStructureCache structureCache,
        Action<EnduranceFailure> reportFailure,
        Stopwatch runStopwatch,
        LatencyRecorder recorder)
        : base(adapter, manifests, archivePaths, fileCache, structureCache, reportFailure, runStopwatch)
    {
        _recorder = recorder;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        Task[] tasks = new Task[5];
        // 2 linear access latency
        for (int i = 0; i < 2; i++)
            tasks[i] = RunWorkloadLoopAsync("LinearLatency", i, ct, LinearLatencyAsync);
        // 2 random access latency
        for (int i = 2; i < 4; i++)
            tasks[i] = RunWorkloadLoopAsync("RandomLatency", i, ct, RandomLatencyAsync);
        // 1 partial read latency
        tasks[4] = RunWorkloadLoopAsync("PartialReadLatency", 4, ct, PartialReadLatencyAsync);

        await Task.WhenAll(tasks);
    }

    public override void PrintReport(ITestOutputHelper output)
    {
        base.PrintReport(output);
        _recorder.PrintReport(output);
    }

    private async Task LinearLatencyAsync(Random rng, CancellationToken ct)
    {
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        if (files.Count == 0) return;

        var (name, size) = files[rng.Next(files.Count)];
        string filePath = $"{archivePath}/{name}";
        bool isSmall = size < 1024 * 1024;

        // Determine cache hit/miss
        bool isMiss = _seenFiles.TryAdd(filePath, true);

        byte[] buf = new byte[size];
        Stopwatch sw = Stopwatch.StartNew();
        int read = await Adapter.GuardedReadFileAsync(filePath, buf, 0, ct);
        sw.Stop();

        Interlocked.Increment(ref Result.TotalOperations);

        string hitMiss = isMiss ? "CacheMiss" : "CacheHit";
        string sizeCategory = isSmall ? "Small" : "Large";
        _recorder.Record($"{hitMiss}.{sizeCategory}", sw.Elapsed.TotalMilliseconds);
        _recorder.Record("Linear", sw.Elapsed.TotalMilliseconds);

        if (read > 0)
            VerifyFullFile(archivePath, name, buf, read, "LinearLatency", 0);
    }

    private async Task RandomLatencyAsync(Random rng, CancellationToken ct)
    {
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        var file = files.FirstOrDefault(f => f.size > 65536);
        if (file.name == null) return;

        string filePath = $"{archivePath}/{file.name}";

        // Ensure file is cached first (read once)
        if (_seenFiles.TryAdd(filePath, true))
        {
            byte[] warmup = new byte[file.size];
            await Adapter.GuardedReadFileAsync(filePath, warmup, 0, ct);
        }

        // Now measure random offset read
        long offset = (long)(rng.NextDouble() * Math.Max(0, file.size - 65536));
        byte[] buf = new byte[65536];

        Stopwatch sw = Stopwatch.StartNew();
        int read = await Adapter.GuardedReadFileAsync(filePath, buf, offset, ct);
        sw.Stop();

        Interlocked.Increment(ref Result.TotalOperations);
        _recorder.Record("Random", sw.Elapsed.TotalMilliseconds);

        if (read > 0)
            Interlocked.Increment(ref Result.VerifiedOperations);
    }

    private async Task PartialReadLatencyAsync(Random rng, CancellationToken ct)
    {
        string archivePath = RandomArchive(rng);
        if (!Manifests.TryGetValue(archivePath, out var manifest)) return;
        if (manifest.PartialSamples.Count == 0) return;

        var sample = manifest.PartialSamples[rng.Next(manifest.PartialSamples.Count)];
        byte[] buf = new byte[sample.Length];

        Stopwatch sw = Stopwatch.StartNew();
        int read = await Adapter.GuardedReadFileAsync(
            $"{archivePath}/{sample.FileName}", buf, sample.Offset, ct);
        sw.Stop();

        Interlocked.Increment(ref Result.TotalOperations);
        _recorder.Record("PartialRead", sw.Elapsed.TotalMilliseconds);

        if (read == sample.Length)
            VerifyPartialRead(archivePath, sample.FileName, buf, read,
                sample.Offset, "PartialReadLatency", 0);
    }
}
