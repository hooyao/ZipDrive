using System.Collections.Concurrent;
using System.Diagnostics;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests.Suites;

/// <summary>
/// Partial/random-offset reads verified against pre-computed partial samples.
/// Includes chunk boundary crossing, multi-chunk spanning, and binary search simulation.
/// </summary>
public sealed class PartialReadSuite : EnduranceSuiteBase
{
    public override string Name => "PartialReadSuite";
    public override int TaskCount => 20;

    public PartialReadSuite(
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
        // 8 verified partial readers (match samples)
        // 4 chunk boundary crossers
        // 4 multi-chunk span readers
        // 4 binary search simulators
        Task[] tasks = new Task[20];
        for (int i = 0; i < 8; i++)
            tasks[i] = RunWorkloadLoopAsync("VerifiedPartialRead", i, ct, VerifiedPartialReadAsync);
        for (int i = 8; i < 12; i++)
            tasks[i] = RunWorkloadLoopAsync("ChunkBoundaryCross", i, ct, ChunkBoundaryCrossAsync);
        for (int i = 12; i < 16; i++)
            tasks[i] = RunWorkloadLoopAsync("MultiChunkSpan", i, ct, MultiChunkSpanAsync);
        for (int i = 16; i < 20; i++)
            tasks[i] = RunWorkloadLoopAsync("BinarySearchSim", i, ct, BinarySearchSimAsync);

        await Task.WhenAll(tasks);
    }

    private async Task VerifiedPartialReadAsync(Random rng, CancellationToken ct)
    {
        string archivePath = RandomArchive(rng);
        if (!Manifests.TryGetValue(archivePath, out ZipManifest? manifest)) return;
        if (manifest.PartialSamples.Count == 0) return;

        var sample = manifest.PartialSamples[rng.Next(manifest.PartialSamples.Count)];
        byte[] buf = new byte[sample.Length];
        int read = await Vfs.ReadFileAsync(
            $"{archivePath}/{sample.FileName}", buf, sample.Offset, ct);
        Interlocked.Increment(ref Result.TotalOperations);

        if (read == sample.Length)
            VerifyPartialRead(archivePath, sample.FileName, buf, read,
                sample.Offset, "VerifiedPartialRead", 0);
    }

    private async Task ChunkBoundaryCrossAsync(Random rng, CancellationToken ct)
    {
        // Read 64KB straddling a 1MB chunk boundary
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        var file = files.FirstOrDefault(f => f.size > 1024 * 1024 + 65536);
        if (file.name == null) return;

        long offset = 1024 * 1024 - 32768; // Straddle 1MB boundary
        byte[] buf = new byte[65536];
        int read = await Vfs.ReadFileAsync($"{archivePath}/{file.name}", buf, offset, ct);
        Interlocked.Increment(ref Result.TotalOperations);

        if (read > 0)
            VerifyPartialRead(archivePath, file.name, buf, read, offset,
                "ChunkBoundaryCross", 0);
    }

    private async Task MultiChunkSpanAsync(Random rng, CancellationToken ct)
    {
        // Read 3MB spanning multiple 1MB chunks
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        var file = files.FirstOrDefault(f => f.size > 4 * 1024 * 1024);
        if (file.name == null) return;

        int readSize = 3 * 1024 * 1024; // 3MB
        long maxOffset = file.size - readSize;
        if (maxOffset <= 0) return;

        long offset = (long)(rng.NextDouble() * maxOffset);
        byte[] buf = new byte[readSize];
        int read = await Vfs.ReadFileAsync($"{archivePath}/{file.name}", buf, offset, ct);
        Interlocked.Increment(ref Result.TotalOperations);

        // No partial sample match expected for arbitrary offsets — just verify no crash
        if (read > 0)
            Interlocked.Increment(ref Result.VerifiedOperations);
    }

    private async Task BinarySearchSimAsync(Random rng, CancellationToken ct)
    {
        // Read 1 byte at many random offsets (simulates binary search)
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        var file = files.FirstOrDefault(f => f.size > 1024);
        if (file.name == null) return;

        string filePath = $"{archivePath}/{file.name}";
        byte[] buf = new byte[1];
        for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
        {
            long offset = (long)(rng.NextDouble() * (file.size - 1));
            int read = await Vfs.ReadFileAsync(filePath, buf, offset, ct);
            Interlocked.Increment(ref Result.TotalOperations);
            if (read > 0)
                Interlocked.Increment(ref Result.VerifiedOperations);
        }
    }
}
