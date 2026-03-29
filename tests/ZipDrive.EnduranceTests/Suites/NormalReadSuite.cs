using System.Collections.Concurrent;
using System.Diagnostics;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Infrastructure.FileSystem;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests.Suites;

/// <summary>
/// Normal read patterns: random file browser, sequential chunked reader (4KB/64KB),
/// hot file access. All reads verified with full-file SHA-256.
/// </summary>
public sealed class NormalReadSuite : EnduranceSuiteBase
{
    public override string Name => "NormalReadSuite";
    public override int TaskCount => 25;

    public NormalReadSuite(
        DokanFileSystemAdapter adapter,
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
        // 10 random file browsers
        // 5 sequential readers (4KB)
        // 5 sequential readers (64KB)
        // 5 hot file hammerers
        Task[] tasks = new Task[25];
        for (int i = 0; i < 10; i++)
            tasks[i] = RunWorkloadLoopAsync("RandomBrowser", i, ct, RandomBrowserAsync);
        for (int i = 10; i < 15; i++)
            tasks[i] = RunWorkloadLoopAsync("SequentialReader4K", i, ct,
                (rng, token) => SequentialReaderAsync(rng, token, 4096, i));
        for (int i = 15; i < 20; i++)
            tasks[i] = RunWorkloadLoopAsync("SequentialReader64K", i, ct,
                (rng, token) => SequentialReaderAsync(rng, token, 65536, i));
        for (int i = 20; i < 25; i++)
            tasks[i] = RunWorkloadLoopAsync("HotFileHammerer", i, ct, HotFileHammererAsync);

        await Task.WhenAll(tasks);
    }

    private async Task RandomBrowserAsync(Random rng, CancellationToken ct)
    {
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        if (files.Count == 0) return;

        var (name, size) = files[rng.Next(files.Count)];
        byte[] buf = new byte[size];
        int read = await Adapter.GuardedReadFileAsync($"{archivePath}/{name}", buf, 0, ct);
        Interlocked.Increment(ref Result.TotalOperations);

        if (read > 0)
            VerifyFullFile(archivePath, name, buf, read, "RandomBrowser", 0);
    }

    private async Task SequentialReaderAsync(Random rng, CancellationToken ct, int chunkSize, int taskId)
    {
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        var file = files.FirstOrDefault(f => f.size > chunkSize);
        if (file.name == null) return;

        string filePath = $"{archivePath}/{file.name}";
        byte[] fullContent = new byte[file.size];
        byte[] chunk = new byte[chunkSize];
        long offset = 0;

        while (!ct.IsCancellationRequested)
        {
            int read = await Adapter.GuardedReadFileAsync(filePath, chunk, offset, ct);
            if (read == 0) break;
            if (offset + read <= fullContent.Length)
                Array.Copy(chunk, 0, fullContent, offset, read);
            offset += read;
            Interlocked.Increment(ref Result.TotalOperations);
        }

        if (offset > 0)
            VerifyFullFile(archivePath, file.name, fullContent, (int)offset,
                $"SequentialReader{chunkSize / 1024}K", taskId);
    }

    private async Task HotFileHammererAsync(Random rng, CancellationToken ct)
    {
        // Pick a file and read it repeatedly to exercise cache hits
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        if (files.Count == 0) return;

        var (name, size) = files[rng.Next(files.Count)];
        string filePath = $"{archivePath}/{name}";

        for (int i = 0; i < 10 && !ct.IsCancellationRequested; i++)
        {
            byte[] buf = new byte[size];
            int read = await Adapter.GuardedReadFileAsync(filePath, buf, 0, ct);
            Interlocked.Increment(ref Result.TotalOperations);
            if (read > 0)
                VerifyFullFile(archivePath, name, buf, read, "HotFileHammerer", 0);
        }
    }
}
