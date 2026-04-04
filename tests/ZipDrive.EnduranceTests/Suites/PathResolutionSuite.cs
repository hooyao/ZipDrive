using System.Collections.Concurrent;
using System.Diagnostics;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Infrastructure.FileSystem;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests.Suites;

/// <summary>
/// Path resolution stress: FileExistsAsync, DirectoryExistsAsync, GetFileInfoAsync,
/// nested directory traversal, non-existent path handling.
/// </summary>
public sealed class PathResolutionSuite : EnduranceSuiteBase
{
    public override string Name => "PathResolutionSuite";
    public override int TaskCount => 8;

    public PathResolutionSuite(
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
        Task[] tasks = new Task[8];
        // 3 existence check stress
        for (int i = 0; i < 3; i++)
            tasks[i] = RunWorkloadLoopAsync("ExistenceCheck", i, ct, ExistenceCheckAsync);
        // 2 file metadata query
        for (int i = 3; i < 5; i++)
            tasks[i] = RunWorkloadLoopAsync("FileMetadata", i, ct, FileMetadataAsync);
        // 2 directory traversal
        for (int i = 5; i < 7; i++)
            tasks[i] = RunWorkloadLoopAsync("DirectoryTraversal", i, ct, DirectoryTraversalAsync);
        // 1 non-existent path
        tasks[7] = RunWorkloadLoopAsync("NonExistentPath", 7, ct, NonExistentPathAsync);

        await Task.WhenAll(tasks);
    }

    private async Task ExistenceCheckAsync(Random rng, CancellationToken ct)
    {
        string archivePath = RandomArchive(rng);
        await Adapter.GuardedDirectoryExistsAsync(archivePath, ct);
        Interlocked.Increment(ref Result.TotalOperations);

        var files = await GetFilesAsync(archivePath, ct);
        foreach (var (name, _) in files.Take(5))
        {
            if (ct.IsCancellationRequested) return;
            await Adapter.GuardedFileExistsAsync($"{archivePath}/{name}", ct);
            Interlocked.Increment(ref Result.TotalOperations);
        }
    }

    private async Task FileMetadataAsync(Random rng, CancellationToken ct)
    {
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, ct);
        if (files.Count == 0) return;

        var (name, _) = files[rng.Next(files.Count)];
        await Adapter.GuardedGetFileInfoAsync($"{archivePath}/{name}", ct);
        Interlocked.Increment(ref Result.TotalOperations);
        Interlocked.Increment(ref Result.VerifiedOperations);
    }

    private async Task DirectoryTraversalAsync(Random rng, CancellationToken ct)
    {
        // Recursively list directories inside an archive
        string archivePath = RandomArchive(rng);
        await TraverseAsync(archivePath, 0, ct);
    }

    private async Task TraverseAsync(string path, int depth, CancellationToken ct)
    {
        if (depth > 5 || ct.IsCancellationRequested) return;

        var entries = await Adapter.GuardedListDirectoryAsync(path, ct);
        Interlocked.Increment(ref Result.TotalOperations);

        foreach (var entry in entries.Where(e => e.IsDirectory).Take(3))
        {
            if (ct.IsCancellationRequested) return;
            string childPath = $"{path}/{entry.Name}";
            await TraverseAsync(childPath, depth + 1, ct);
        }
    }

    private async Task NonExistentPathAsync(Random rng, CancellationToken ct)
    {
        string archivePath = RandomArchive(rng);
        string fakePath = $"{archivePath}/__does_not_exist_{rng.Next()}.bin";

        bool exists = await Adapter.GuardedFileExistsAsync(fakePath, ct);
        Interlocked.Increment(ref Result.TotalOperations);

        // Should not exist
        if (!exists)
            Interlocked.Increment(ref Result.VerifiedOperations);
    }
}
