using System.Collections.Concurrent;
using System.Diagnostics;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Infrastructure.FileSystem;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests.Suites;

/// <summary>
/// Concurrency stress: thundering herd (same file), parallel materialization (different files),
/// concurrent structure cache access.
/// </summary>
public sealed class ConcurrencyStressSuite : EnduranceSuiteBase
{
    public override string Name => "ConcurrencyStressSuite";
    public override int TaskCount => 20;

    public ConcurrencyStressSuite(
        WinFspFileSystemAdapter adapter,
        ConcurrentDictionary<string, ZipManifest> manifests,
        List<string> archivePaths,
        FileContentCache fileCache,
        IArchiveStructureCache structureCache,
        Action<EnduranceFailure> reportFailure,
        Stopwatch runStopwatch)
        : base(adapter, manifests, archivePaths, fileCache, structureCache, reportFailure, runStopwatch)
    {
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        // 8 thundering herd tasks
        // 7 parallel materialization tasks
        // 5 concurrent structure cache tasks
        Task[] tasks = new Task[20];
        for (int i = 0; i < 8; i++)
            tasks[i] = RunWorkloadLoopAsync("ThunderingHerd", i, ct, ThunderingHerdAsync);
        for (int i = 8; i < 15; i++)
            tasks[i] = RunWorkloadLoopAsync("ParallelMaterialization", i, ct, ParallelMaterializationAsync);
        for (int i = 15; i < 20; i++)
            tasks[i] = RunWorkloadLoopAsync("ConcurrentStructure", i, ct, ConcurrentStructureAsync);

        await Task.WhenAll(tasks);
    }

    private async Task ThunderingHerdAsync(Random rng, CancellationToken ct)
    {
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        if (files.Count == 0) return;

        var (name, size) = files[rng.Next(files.Count)];
        string filePath = $"{archivePath}/{name}";

        // 20 concurrent reads of the same file
        var readers = Enumerable.Range(0, 20).Select(async _ =>
        {
            byte[] buf = new byte[size];
            int read = await Adapter.GuardedReadFileAsync(filePath, buf, 0, ct);
            Interlocked.Increment(ref Result.TotalOperations);
            if (read > 0)
                VerifyFullFile(archivePath, name, buf, read, "ThunderingHerd", 0);
        }).ToArray();

        await Task.WhenAll(readers);
    }

    private async Task ParallelMaterializationAsync(Random rng, CancellationToken ct)
    {
        // Collect files from multiple archives
        List<(string archivePath, string filePath, string fileName, long size)> targets = new();

        for (int attempt = 0; attempt < 30 && targets.Count < 10; attempt++)
        {
            string archivePath = RandomArchive(rng);
            var files = await GetFilesAsync(archivePath, ct);
            foreach (var (name, size) in files)
            {
                string filePath = $"{archivePath}/{name}";
                if (targets.All(t => t.filePath != filePath))
                {
                    targets.Add((archivePath, filePath, name, size));
                    if (targets.Count >= 10) break;
                }
            }
        }

        if (targets.Count == 0) return;

        var readers = targets.Select(async t =>
        {
            byte[] buf = new byte[t.size];
            int read = await Adapter.GuardedReadFileAsync(t.filePath, buf, 0, ct);
            Interlocked.Increment(ref Result.TotalOperations);
            if (read > 0)
                VerifyFullFile(t.archivePath, t.fileName, buf, read, "ParallelMaterialization", 0);
        }).ToArray();

        await Task.WhenAll(readers);
    }

    private async Task ConcurrentStructureAsync(Random rng, CancellationToken ct)
    {
        // Multiple concurrent structure cache hits on the same archive
        string archivePath = RandomArchive(rng);
        var readers = Enumerable.Range(0, 5).Select(async _ =>
        {
            await Adapter.GuardedListDirectoryAsync(archivePath, ct);
            Interlocked.Increment(ref Result.TotalOperations);
        }).ToArray();

        await Task.WhenAll(readers);
    }
}
