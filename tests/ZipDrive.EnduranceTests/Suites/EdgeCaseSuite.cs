using System.Collections.Concurrent;
using System.Diagnostics;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Infrastructure.FileSystem;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests.Suites;

/// <summary>
/// Edge cases: zero-byte file, single-byte file, exact cutoff boundary,
/// read at EOF, Store-compressed entry.
/// </summary>
public sealed class EdgeCaseSuite : EnduranceSuiteBase
{
    public override string Name => "EdgeCaseSuite";
    public override int TaskCount => 10;

    public EdgeCaseSuite(
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
        Task[] tasks = new Task[10];
        // 3 zero-byte file readers
        for (int i = 0; i < 3; i++)
            tasks[i] = RunWorkloadLoopAsync("ZeroByteFile", i, ct, ZeroByteFileAsync);
        // 2 single-byte file readers
        for (int i = 3; i < 5; i++)
            tasks[i] = RunWorkloadLoopAsync("SingleByteFile", i, ct, SingleByteFileAsync);
        // 2 read-at-EOF
        for (int i = 5; i < 7; i++)
            tasks[i] = RunWorkloadLoopAsync("ReadAtEOF", i, ct, ReadAtEofAsync);
        // 2 exact cutoff boundary
        for (int i = 7; i < 9; i++)
            tasks[i] = RunWorkloadLoopAsync("ExactCutoff", i, ct, ExactCutoffAsync);
        // 1 store-compressed
        tasks[9] = RunWorkloadLoopAsync("StoreCompressed", 9, ct, StoreCompressedAsync);

        await Task.WhenAll(tasks);
    }

    private async Task ZeroByteFileAsync(Random rng, CancellationToken ct)
    {
        // Find edge case archives containing empty files
        foreach (string archivePath in ArchivePaths)
        {
            if (ct.IsCancellationRequested) return;
            if (!Manifests.TryGetValue(archivePath, out var manifest)) continue;

            var emptyEntry = manifest.Entries.FirstOrDefault(
                e => !e.IsDirectory && e.UncompressedSize == 0);
            if (emptyEntry == null) continue;

            byte[] buf = new byte[1];
            int read = await Adapter.GuardedReadFileAsync(
                $"{archivePath}/{emptyEntry.FileName}", buf, 0, ct);
            Interlocked.Increment(ref Result.TotalOperations);

            if (read == 0)
                Interlocked.Increment(ref Result.VerifiedOperations);
            return;
        }
    }

    private async Task SingleByteFileAsync(Random rng, CancellationToken ct)
    {
        foreach (string archivePath in ArchivePaths)
        {
            if (ct.IsCancellationRequested) return;
            if (!Manifests.TryGetValue(archivePath, out var manifest)) continue;

            var entry = manifest.Entries.FirstOrDefault(
                e => !e.IsDirectory && e.UncompressedSize == 1);
            if (entry == null) continue;

            byte[] buf = new byte[1];
            int read = await Adapter.GuardedReadFileAsync(
                $"{archivePath}/{entry.FileName}", buf, 0, ct);
            Interlocked.Increment(ref Result.TotalOperations);

            if (read == 1)
            {
                VerifyFullFile(archivePath, entry.FileName, buf, read, "SingleByteFile", 0);
            }
            return;
        }
    }

    private async Task ReadAtEofAsync(Random rng, CancellationToken ct)
    {
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        if (files.Count == 0) return;

        var (name, size) = files[rng.Next(files.Count)];
        if (size == 0) return;

        // Read at exact EOF offset — should return 0 bytes
        byte[] buf = new byte[1024];
        int read = await Adapter.GuardedReadFileAsync($"{archivePath}/{name}", buf, size, ct);
        Interlocked.Increment(ref Result.TotalOperations);

        if (read == 0)
            Interlocked.Increment(ref Result.VerifiedOperations);
    }

    private async Task ExactCutoffAsync(Random rng, CancellationToken ct)
    {
        // Look for files at exact cutoff boundary (1MB)
        foreach (string archivePath in ArchivePaths)
        {
            if (ct.IsCancellationRequested) return;
            if (!Manifests.TryGetValue(archivePath, out var manifest)) continue;

            var entry = manifest.Entries.FirstOrDefault(
                e => !e.IsDirectory && e.UncompressedSize == 1024 * 1024);
            if (entry == null) continue;

            byte[] buf = new byte[entry.UncompressedSize];
            int read = await Adapter.GuardedReadFileAsync(
                $"{archivePath}/{entry.FileName}", buf, 0, ct);
            Interlocked.Increment(ref Result.TotalOperations);

            if (read > 0)
                VerifyFullFile(archivePath, entry.FileName, buf, read, "ExactCutoff", 0);
            return;
        }
    }

    private async Task StoreCompressedAsync(Random rng, CancellationToken ct)
    {
        // Look for Store-compressed entries
        foreach (string archivePath in ArchivePaths)
        {
            if (ct.IsCancellationRequested) return;
            if (!Manifests.TryGetValue(archivePath, out var manifest)) continue;

            var entry = manifest.Entries.FirstOrDefault(
                e => !e.IsDirectory && e.CompressionMethod == "Store" && e.UncompressedSize > 0);
            if (entry == null) continue;

            byte[] buf = new byte[entry.UncompressedSize];
            int read = await Adapter.GuardedReadFileAsync(
                $"{archivePath}/{entry.FileName}", buf, 0, ct);
            Interlocked.Increment(ref Result.TotalOperations);

            if (read > 0)
                VerifyFullFile(archivePath, entry.FileName, buf, read, "StoreCompressed", 0);
            return;
        }
    }
}
