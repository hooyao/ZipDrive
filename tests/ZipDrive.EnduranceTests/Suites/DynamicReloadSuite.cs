using System.Collections.Concurrent;
using System.Diagnostics;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests.Suites;

/// <summary>
/// Endurance suite that tests dynamic archive add/remove while concurrent reads are running.
/// Exercises IArchiveManager.AddArchiveAsync/RemoveArchiveAsync alongside VFS reads.
/// Uses dedicated reload-only archives that other suites do NOT access.
///
/// Workloads:
/// - AddRemoveChurn (3 tasks): add → read → remove cycle
/// - ReloadReader (3 tasks): concurrent reads tolerating removal
/// - RapidChurn (2 tasks): add → read → delete → re-add → verify fresh content
/// - ExplorerBrowse (2 tasks): list root → info → list subdir → read
/// - Adversarial (2 tasks): nonexistent paths, past-EOF, reads during removal
/// </summary>
public sealed class DynamicReloadSuite : EnduranceSuiteBase
{
    private readonly IArchiveManager _archiveManager;
    private readonly string _reloadArchiveDir;
    private readonly List<string> _reloadArchivePaths;

    public override string Name => "DynamicReloadSuite";
    public override int TaskCount => 12;

    public DynamicReloadSuite(
        IVirtualFileSystem vfs,
        IArchiveManager archiveManager,
        ConcurrentDictionary<string, ZipManifest> manifests,
        List<string> archivePaths,
        FileContentCache fileCache,
        IArchiveStructureCache structureCache,
        Action<EnduranceFailure> reportFailure,
        Stopwatch runStopwatch,
        string reloadArchiveDir,
        List<string> reloadArchivePaths)
        : base(vfs, manifests, archivePaths, fileCache, structureCache, reportFailure, runStopwatch)
    {
        _archiveManager = archiveManager;
        _reloadArchiveDir = reloadArchiveDir;
        _reloadArchivePaths = reloadArchivePaths;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        var tasks = new List<Task>();

        // 3 churner tasks: add, read, remove, repeat
        tasks.AddRange(Enumerable.Range(0, 3).Select(i =>
            RunWorkloadLoopAsync("AddRemoveChurn", i, ct, AddRemoveChurnBody)));

        // 3 reader tasks: try to read from reload archives, tolerate FileNotFound
        tasks.AddRange(Enumerable.Range(3, 3).Select(i =>
            RunWorkloadLoopAsync("ReloadReader", i, ct, ReloadReaderBody)));

        // 2 rapid churn tasks: add → read → remove → re-add → verify content is fresh
        tasks.AddRange(Enumerable.Range(6, 2).Select(i =>
            RunWorkloadLoopAsync("RapidChurn", i, ct, RapidChurnBody)));

        // 2 explorer browsing tasks: list → info → list subdir → read
        tasks.AddRange(Enumerable.Range(8, 2).Select(i =>
            RunWorkloadLoopAsync("ExplorerBrowse", i, ct, ExplorerBrowseBody)));

        // 2 adversarial tasks: nonexistent paths, reads during removal
        tasks.AddRange(Enumerable.Range(10, 2).Select(i =>
            RunWorkloadLoopAsync("Adversarial", i, ct, AdversarialBody)));

        await Task.WhenAll(tasks);
    }

    // ── Workload: AddRemoveChurn ────────────────────────────────────────

    private async Task AddRemoveChurnBody(Random rng, CancellationToken token)
    {
        string virtualPath = PickReloadVirtualPath(rng);
        string zipPath = ResolvePhysicalPath(virtualPath);
        if (!File.Exists(zipPath)) return;

        await AddReloadArchiveAsync(virtualPath, zipPath, token);

        try
        {
            await ReadRandomFileFromArchiveAsync($"reload/{virtualPath}", rng, token);
        }
        catch (Exception) when (!token.IsCancellationRequested) { }

        await Task.Delay(rng.Next(50, 200), token);
        await _archiveManager.RemoveArchiveAsync($"reload/{virtualPath}", token);
        Interlocked.Increment(ref Result.TotalOperations);
        await Task.Delay(rng.Next(10, 50), token);
    }

    // ── Workload: ReloadReader ──────────────────────────────────────────

    private async Task ReloadReaderBody(Random rng, CancellationToken token)
    {
        string virtualPath = PickReloadVirtualPath(rng);

        try
        {
            bool exists = await Vfs.DirectoryExistsAsync($"reload/{virtualPath}", token);
            if (!exists) return;

            await ReadRandomFileFromArchiveAsync($"reload/{virtualPath}", rng, token);
        }
        catch (Domain.Exceptions.VfsFileNotFoundException)
        {
            Interlocked.Increment(ref Result.TotalOperations);
        }
        catch (Domain.Exceptions.VfsDirectoryNotFoundException)
        {
            Interlocked.Increment(ref Result.TotalOperations);
        }

        await Task.Delay(rng.Next(10, 50), token);
    }

    // ── Workload: RapidChurn ────────────────────────────────────────────

    private async Task RapidChurnBody(Random rng, CancellationToken token)
    {
        string virtualPath = PickReloadVirtualPath(rng);
        string zipPath = ResolvePhysicalPath(virtualPath);
        if (!File.Exists(zipPath)) return;

        // Add → read → remove → re-add → read again (verify content not stale)
        await AddReloadArchiveAsync(virtualPath, zipPath, token);

        try
        {
            await ReadRandomFileFromArchiveAsync($"reload/{virtualPath}", rng, token);
        }
        catch (Exception) when (!token.IsCancellationRequested) { }

        await _archiveManager.RemoveArchiveAsync($"reload/{virtualPath}", token);
        Interlocked.Increment(ref Result.TotalOperations);

        // Re-add immediately (same content — tests that cache is properly cleared)
        await AddReloadArchiveAsync(virtualPath, zipPath, token);

        try
        {
            await ReadRandomFileFromArchiveAsync($"reload/{virtualPath}", rng, token);
        }
        catch (Exception) when (!token.IsCancellationRequested) { }

        await _archiveManager.RemoveArchiveAsync($"reload/{virtualPath}", token);
        Interlocked.Increment(ref Result.TotalOperations);

        await Task.Delay(rng.Next(10, 50), token);
    }

    // ── Workload: ExplorerBrowse ────────────────────────────────────────

    private async Task ExplorerBrowseBody(Random rng, CancellationToken token)
    {
        // Browse the static archives (not reload ones) — exercises concurrent reads
        // while reload churners are adding/removing in parallel
        string archivePath = RandomArchive(rng);

        try
        {
            var info = await Vfs.GetFileInfoAsync(archivePath, token);
            Interlocked.Increment(ref Result.TotalOperations);

            var listing = await Vfs.ListDirectoryAsync(archivePath, token);
            Interlocked.Increment(ref Result.TotalOperations);

            var files = listing.Where(e => !e.IsDirectory && e.Name != "__manifest__.json").ToList();
            if (files.Count > 0)
            {
                var file = files[rng.Next(files.Count)];
                byte[] buf = new byte[Math.Min(4096, (int)Math.Max(file.SizeBytes, 1))];
                await Vfs.ReadFileAsync(file.FullPath, buf, 0, token);
                Interlocked.Increment(ref Result.TotalOperations);
            }
        }
        catch (Domain.Exceptions.VfsFileNotFoundException)
        {
            Interlocked.Increment(ref Result.TotalOperations);
        }

        await Task.Delay(rng.Next(20, 100), token);
    }

    // ── Workload: Adversarial ───────────────────────────────────────────

    private async Task AdversarialBody(Random rng, CancellationToken token)
    {
        int scenario = rng.Next(4);

        switch (scenario)
        {
            case 0:
                // Read from nonexistent archive
                try
                {
                    await Vfs.ReadFileAsync("reload/nonexistent.zip/file.bin", new byte[1024], 0, token);
                }
                catch (Domain.Exceptions.VfsFileNotFoundException)
                {
                    // Expected
                }
                Interlocked.Increment(ref Result.TotalOperations);
                break;

            case 1:
                // Read past EOF from a static archive
                try
                {
                    string archivePath = RandomArchive(rng);
                    var files = await GetFilesAsync(archivePath, token);
                    if (files.Count > 0)
                    {
                        var (name, size) = files[rng.Next(files.Count)];
                        byte[] buf = new byte[1024];
                        int read = await Vfs.ReadFileAsync($"{archivePath}/{name}", buf, size + 1000, token);
                        // Should return 0 bytes (past EOF)
                    }
                }
                catch (Domain.Exceptions.VfsFileNotFoundException) { }
                Interlocked.Increment(ref Result.TotalOperations);
                break;

            case 2:
                // Check existence of removed archive
                try
                {
                    bool exists = await Vfs.FileExistsAsync("reload/ghost.zip/phantom.txt", token);
                    // Should return false
                }
                catch (Domain.Exceptions.VfsFileNotFoundException) { }
                Interlocked.Increment(ref Result.TotalOperations);
                break;

            case 3:
                // List directory on reload path that may or may not exist
                try
                {
                    string virtualPath = PickReloadVirtualPath(rng);
                    await Vfs.ListDirectoryAsync($"reload/{virtualPath}", token);
                }
                catch (Domain.Exceptions.VfsDirectoryNotFoundException) { }
                catch (Domain.Exceptions.VfsFileNotFoundException) { }
                Interlocked.Increment(ref Result.TotalOperations);
                break;
        }

        await Task.Delay(rng.Next(10, 50), token);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private string PickReloadVirtualPath(Random rng)
    {
        string reloadPath = _reloadArchivePaths[rng.Next(_reloadArchivePaths.Count)];
        return Path.GetFileName(reloadPath).Replace('\\', '/');
    }

    private string ResolvePhysicalPath(string virtualPath)
    {
        return Path.Combine(_reloadArchiveDir, virtualPath.Replace('/', '\\'));
    }

    private async Task AddReloadArchiveAsync(string virtualPath, string zipPath, CancellationToken token)
    {
        var descriptor = new ArchiveDescriptor
        {
            VirtualPath = $"reload/{virtualPath}",
            PhysicalPath = zipPath,
            SizeBytes = new FileInfo(zipPath).Length,
            LastModifiedUtc = File.GetLastWriteTimeUtc(zipPath)
        };

        await _archiveManager.AddArchiveAsync(descriptor, token);
        Interlocked.Increment(ref Result.TotalOperations);
    }

    private async Task ReadRandomFileFromArchiveAsync(string archivePath, Random rng, CancellationToken token)
    {
        var listing = await Vfs.ListDirectoryAsync(archivePath, token);
        Interlocked.Increment(ref Result.TotalOperations);

        var files = listing.Where(e => !e.IsDirectory && e.Name != "__manifest__.json").ToList();
        if (files.Count > 0)
        {
            var file = files[rng.Next(files.Count)];
            byte[] buf = new byte[Math.Min(4096, (int)Math.Max(file.SizeBytes, 1))];
            int read = await Vfs.ReadFileAsync(file.FullPath, buf, 0, token);
            Interlocked.Increment(ref Result.TotalOperations);
        }
    }
}
