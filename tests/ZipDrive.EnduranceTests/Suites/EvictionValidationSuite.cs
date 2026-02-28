using System.Collections.Concurrent;
using System.Diagnostics;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests.Suites;

/// <summary>
/// Eviction validation: cold scan, re-read after eviction/TTL expiry,
/// burst-idle cycling, interleaved hot/cold access.
/// </summary>
public sealed class EvictionValidationSuite : EnduranceSuiteBase
{
    public override string Name => "EvictionValidationSuite";
    public override int TaskCount => 10;

    public EvictionValidationSuite(
        IVirtualFileSystem vfs,
        ConcurrentDictionary<string, ZipManifest> manifests,
        List<string> archivePaths,
        FileContentCache fileCache,
        IArchiveStructureCache structureCache,
        Action<EnduranceFailure> reportFailure,
        Stopwatch runStopwatch)
        : base(vfs, manifests, archivePaths, fileCache, structureCache, reportFailure, runStopwatch)
    {
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        Task[] tasks = new Task[10];
        // 3 cold scanners
        for (int i = 0; i < 3; i++)
            tasks[i] = RunWorkloadLoopAsync("ColdScan", i, ct, ColdScanAsync);
        // 3 re-read after TTL expiry
        for (int i = 3; i < 6; i++)
            tasks[i] = RunWorkloadLoopAsync("ReReadAfterTTL", i, ct, ReReadAfterTtlAsync);
        // 2 burst-idle cyclers
        for (int i = 6; i < 8; i++)
            tasks[i] = RunWorkloadLoopAsync("BurstIdle", i, ct, BurstIdleAsync);
        // 2 interleaved hot/cold
        for (int i = 8; i < 10; i++)
            tasks[i] = RunWorkloadLoopAsync("InterleavedHotCold", i, ct, InterleavedHotColdAsync);

        await Task.WhenAll(tasks);
    }

    private async Task ColdScanAsync(Random rng, CancellationToken ct)
    {
        // Read all files across all archives sequentially — forces eviction waves
        foreach (string archivePath in ArchivePaths)
        {
            if (ct.IsCancellationRequested) return;
            var files = await GetFilesAsync(archivePath, ct);
            foreach (var (name, size) in files)
            {
                if (ct.IsCancellationRequested) return;
                byte[] buf = new byte[size];
                int read = await Vfs.ReadFileAsync($"{archivePath}/{name}", buf, 0, ct);
                Interlocked.Increment(ref Result.TotalOperations);
                if (read > 0)
                    VerifyFullFile(archivePath, name, buf, read, "ColdScan", 0);
            }
        }
    }

    private async Task ReReadAfterTtlAsync(Random rng, CancellationToken ct)
    {
        // Read a file, wait for TTL to expire, read again
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        if (files.Count == 0) return;

        var (name, size) = files[rng.Next(files.Count)];
        string filePath = $"{archivePath}/{name}";

        // First read
        byte[] buf = new byte[size];
        int read = await Vfs.ReadFileAsync(filePath, buf, 0, ct);
        Interlocked.Increment(ref Result.TotalOperations);
        if (read > 0)
            VerifyFullFile(archivePath, name, buf, read, "ReReadAfterTTL", 0);

        // Wait for TTL expiry (TTL=1 minute, but maintenance runs every 2s)
        await Task.Delay(TimeSpan.FromSeconds(65), ct);

        // Re-read — should re-materialize from ZIP
        buf = new byte[size];
        read = await Vfs.ReadFileAsync(filePath, buf, 0, ct);
        Interlocked.Increment(ref Result.TotalOperations);
        if (read > 0)
            VerifyFullFile(archivePath, name, buf, read, "ReReadAfterTTL", 0);
    }

    private async Task BurstIdleAsync(Random rng, CancellationToken ct)
    {
        // Burst of 50 reads, then idle 30 seconds
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        if (files.Count == 0) return;

        // Burst
        for (int i = 0; i < 50 && !ct.IsCancellationRequested; i++)
        {
            var (name, size) = files[rng.Next(files.Count)];
            byte[] buf = new byte[size];
            int read = await Vfs.ReadFileAsync($"{archivePath}/{name}", buf, 0, ct);
            Interlocked.Increment(ref Result.TotalOperations);
            if (read > 0)
                VerifyFullFile(archivePath, name, buf, read, "BurstIdle", 0);
        }

        // Idle
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
    }

    private async Task InterleavedHotColdAsync(Random rng, CancellationToken ct)
    {
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        if (files.Count < 3) return;

        // 2 "hot" files, rest are "cold"
        var hotFiles = files.Take(2).ToList();
        var coldFiles = files.Skip(2).ToList();

        for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
        {
            // Alternate: hot, hot, cold
            var (name, size) = i % 3 < 2
                ? hotFiles[rng.Next(hotFiles.Count)]
                : coldFiles[rng.Next(coldFiles.Count)];

            byte[] buf = new byte[size];
            int read = await Vfs.ReadFileAsync($"{archivePath}/{name}", buf, 0, ct);
            Interlocked.Increment(ref Result.TotalOperations);
            if (read > 0)
                VerifyFullFile(archivePath, name, buf, read, "InterleavedHotCold", 0);
        }
    }
}
