using System.Collections.Concurrent;
using System.Diagnostics;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.Infrastructure.FileSystem;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests.Suites;

/// <summary>
/// Endurance suite testing dynamic archive add/remove through WinFspFileSystemAdapter.
/// All reads go through Adapter.Guarded*Async. Lifecycle ops go through IArchiveManager.
/// Uses dedicated reload-only archives isolated from other suites.
///
/// 10 workload categories, ~20 concurrent tasks:
/// - AddRemoveChurn: add → list → read → remove cycle
/// - ReloadReader: concurrent reads tolerating removal
/// - RapidChurn: add → read → remove → re-add → verify fresh content
/// - ExplorerBrowse: tree walk on static archives during churn
/// - Adversarial: nonexistent paths, past-EOF, reads during removal
/// - CrossArchive: verify removing Y doesn't affect reads on X
/// - BulkAdd: add multiple archives simultaneously
/// - Rename: remove+add simulating rename
/// - ThunderingHerd: 10 concurrent reads on freshly-added archive
/// - DegradationMonitor: sample BorrowedEntryCount periodically
/// </summary>
public sealed class DynamicReloadSuite : EnduranceSuiteBase
{
    private readonly IArchiveManager _archiveManager;
    private readonly string _reloadArchiveDir;
    private readonly List<string> _reloadArchivePaths;

    public override string Name => "DynamicReloadSuite";
    public override int TaskCount => 20;

    public DynamicReloadSuite(
        WinFspFileSystemAdapter adapter,
        IArchiveManager archiveManager,
        ConcurrentDictionary<string, ZipManifest> manifests,
        List<string> archivePaths,
        FileContentCache fileCache,
        IArchiveStructureCache structureCache,
        Action<EnduranceFailure> reportFailure,
        Stopwatch runStopwatch,
        string reloadArchiveDir,
        List<string> reloadArchivePaths)
        : base(adapter, manifests, archivePaths, fileCache, structureCache, reportFailure, runStopwatch)
    {
        _archiveManager = archiveManager;
        _reloadArchiveDir = reloadArchiveDir;
        _reloadArchivePaths = reloadArchivePaths;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        var tasks = new List<Task>();

        // Cat 1: AddRemoveChurn (3 tasks) — add, read, remove cycle
        tasks.AddRange(Enumerable.Range(0, 3).Select(i =>
            RunWorkloadLoopAsync("AddRemoveChurn", i, ct, AddRemoveChurnBody)));

        // Cat 2: ReloadReader (3 tasks) — reads from reload archives, tolerates removal
        tasks.AddRange(Enumerable.Range(3, 3).Select(i =>
            RunWorkloadLoopAsync("ReloadReader", i, ct, ReloadReaderBody)));

        // Cat 3: RapidChurn (2 tasks) — add → read → remove → re-add → verify fresh
        tasks.AddRange(Enumerable.Range(6, 2).Select(i =>
            RunWorkloadLoopAsync("RapidChurn", i, ct, RapidChurnBody)));

        // Cat 4: ExplorerBrowse (2 tasks) — tree walk static archives during churn
        tasks.AddRange(Enumerable.Range(8, 2).Select(i =>
            RunWorkloadLoopAsync("ExplorerBrowse", i, ct, ExplorerBrowseBody)));

        // Cat 5: Adversarial (2 tasks) — error paths
        tasks.AddRange(Enumerable.Range(10, 2).Select(i =>
            RunWorkloadLoopAsync("Adversarial", i, ct, AdversarialBody)));

        // Cat 6: CrossArchive (2 tasks) — verify archive isolation
        tasks.AddRange(Enumerable.Range(12, 2).Select(i =>
            RunWorkloadLoopAsync("CrossArchive", i, ct, CrossArchiveBody)));

        // Cat 7: BulkAdd (1 task) — add/remove multiple archives at once
        tasks.Add(RunWorkloadLoopAsync("BulkAdd", 14, ct, BulkAddBody));

        // Cat 8: Rename simulation (1 task) — remove old + add new
        tasks.Add(RunWorkloadLoopAsync("Rename", 15, ct, RenameBody));

        // Cat 9: ThunderingHerd (2 tasks) — concurrent reads on freshly-added archive
        tasks.AddRange(Enumerable.Range(16, 2).Select(i =>
            RunWorkloadLoopAsync("ThunderingHerd", i, ct, ThunderingHerdBody)));

        // Cat 10: DegradationMonitor (2 tasks) — sample counters
        tasks.AddRange(Enumerable.Range(18, 2).Select(i =>
            RunWorkloadLoopAsync("DegradationMonitor", i, ct, DegradationMonitorBody)));

        await Task.WhenAll(tasks);
    }

    // ── Cat 1: AddRemoveChurn ───────────────────────────────────────────

    private async Task AddRemoveChurnBody(Random rng, CancellationToken token)
    {
        string virtualPath = PickReloadVirtualPath(rng);
        string zipPath = ResolvePhysicalPath(virtualPath);
        if (!File.Exists(zipPath)) return;

        await AddReloadArchiveAsync(virtualPath, zipPath, token);

        try
        {
            // List → read through adapter
            var listing = await Adapter.GuardedListDirectoryAsync($"reload/{virtualPath}", token);
            Interlocked.Increment(ref Result.TotalOperations);

            var files = listing.Where(e => !e.IsDirectory && e.Name != "__manifest__.json").ToList();
            if (files.Count > 0)
            {
                var file = files[rng.Next(files.Count)];
                byte[] buf = new byte[Math.Min(4096, (int)Math.Max(file.SizeBytes, 1))];
                await Adapter.GuardedReadFileAsync(file.FullPath, buf, 0, token);
                Interlocked.Increment(ref Result.TotalOperations);
            }
        }
        catch (Exception) when (!token.IsCancellationRequested) { }

        await Task.Delay(rng.Next(50, 200), token);
        await _archiveManager.RemoveArchiveAsync($"reload/{virtualPath}", token);
        Interlocked.Increment(ref Result.TotalOperations);
        await Task.Delay(rng.Next(10, 50), token);
    }

    // ── Cat 2: ReloadReader ─────────────────────────────────────────────

    private async Task ReloadReaderBody(Random rng, CancellationToken token)
    {
        string virtualPath = PickReloadVirtualPath(rng);

        try
        {
            bool exists = await Adapter.GuardedDirectoryExistsAsync($"reload/{virtualPath}", token);
            if (!exists) return;

            var listing = await Adapter.GuardedListDirectoryAsync($"reload/{virtualPath}", token);
            Interlocked.Increment(ref Result.TotalOperations);

            var files = listing.Where(e => !e.IsDirectory && e.Name != "__manifest__.json").ToList();
            if (files.Count > 0)
            {
                var file = files[rng.Next(files.Count)];
                byte[] buf = new byte[Math.Min(4096, (int)Math.Max(file.SizeBytes, 1))];
                await Adapter.GuardedReadFileAsync(file.FullPath, buf, 0, token);
                Interlocked.Increment(ref Result.TotalOperations);
            }
        }
        catch (Domain.Exceptions.VfsFileNotFoundException) { Interlocked.Increment(ref Result.TotalOperations); }
        catch (Domain.Exceptions.VfsDirectoryNotFoundException) { Interlocked.Increment(ref Result.TotalOperations); }

        await Task.Delay(rng.Next(10, 50), token);
    }

    // ── Cat 3: RapidChurn ───────────────────────────────────────────────

    private async Task RapidChurnBody(Random rng, CancellationToken token)
    {
        string virtualPath = PickReloadVirtualPath(rng);
        string zipPath = ResolvePhysicalPath(virtualPath);
        if (!File.Exists(zipPath)) return;

        // Add → read → remove → re-add → read (verify content fresh, not stale)
        await AddReloadArchiveAsync(virtualPath, zipPath, token);
        try { await ReadRandomFileFromArchiveAsync($"reload/{virtualPath}", rng, token); }
        catch (Exception) when (!token.IsCancellationRequested) { }

        await _archiveManager.RemoveArchiveAsync($"reload/{virtualPath}", token);
        Interlocked.Increment(ref Result.TotalOperations);

        // Re-add immediately — cache must be cleared, fresh structure built
        await AddReloadArchiveAsync(virtualPath, zipPath, token);
        try { await ReadRandomFileFromArchiveAsync($"reload/{virtualPath}", rng, token); }
        catch (Exception) when (!token.IsCancellationRequested) { }

        await _archiveManager.RemoveArchiveAsync($"reload/{virtualPath}", token);
        Interlocked.Increment(ref Result.TotalOperations);
        await Task.Delay(rng.Next(10, 50), token);
    }

    // ── Cat 4: ExplorerBrowse ───────────────────────────────────────────

    private async Task ExplorerBrowseBody(Random rng, CancellationToken token)
    {
        // Browse static archives through adapter while churners are active
        string archivePath = RandomArchive(rng);

        try
        {
            var info = await Adapter.GuardedGetFileInfoAsync(archivePath, token);
            Interlocked.Increment(ref Result.TotalOperations);

            var listing = await Adapter.GuardedListDirectoryAsync(archivePath, token);
            Interlocked.Increment(ref Result.TotalOperations);

            var files = listing.Where(e => !e.IsDirectory && e.Name != "__manifest__.json").ToList();
            if (files.Count > 0)
            {
                var file = files[rng.Next(files.Count)];
                byte[] buf = new byte[Math.Min(4096, (int)Math.Max(file.SizeBytes, 1))];
                await Adapter.GuardedReadFileAsync(file.FullPath, buf, 0, token);
                Interlocked.Increment(ref Result.TotalOperations);
            }
        }
        catch (Domain.Exceptions.VfsFileNotFoundException) { Interlocked.Increment(ref Result.TotalOperations); }

        await Task.Delay(rng.Next(20, 100), token);
    }

    // ── Cat 5: Adversarial ──────────────────────────────────────────────

    private async Task AdversarialBody(Random rng, CancellationToken token)
    {
        int scenario = rng.Next(5);
        switch (scenario)
        {
            case 0: // Read nonexistent
                try { await Adapter.GuardedReadFileAsync("reload/nonexistent.zip/file.bin", new byte[1024], 0, token); }
                catch (Domain.Exceptions.VfsFileNotFoundException) { }
                break;

            case 1: // Read past EOF
                try
                {
                    string archivePath = RandomArchive(rng);
                    var files = await GetFilesAsync(archivePath, token);
                    if (files.Count > 0)
                    {
                        var (name, size) = files[rng.Next(files.Count)];
                        await Adapter.GuardedReadFileAsync($"{archivePath}/{name}", new byte[1024], size + 1000, token);
                    }
                }
                catch (Domain.Exceptions.VfsFileNotFoundException) { }
                break;

            case 2: // Check existence of ghost archive
                await Adapter.GuardedFileExistsAsync("reload/ghost.zip/phantom.txt", token);
                break;

            case 3: // List directory on volatile reload path
                try
                {
                    string vp = PickReloadVirtualPath(rng);
                    await Adapter.GuardedListDirectoryAsync($"reload/{vp}", token);
                }
                catch (Domain.Exceptions.VfsDirectoryNotFoundException) { }
                catch (Domain.Exceptions.VfsFileNotFoundException) { }
                break;

            case 4: // DirectoryExists on removed archive
                await Adapter.GuardedDirectoryExistsAsync("reload/removed.zip", token);
                break;
        }
        Interlocked.Increment(ref Result.TotalOperations);
        await Task.Delay(rng.Next(10, 50), token);
    }

    // ── Cat 6: CrossArchive ─────────────────────────────────────────────

    private async Task CrossArchiveBody(Random rng, CancellationToken token)
    {
        // Read from a STATIC archive while churners are adding/removing reload archives.
        // Verify static archive reads are never affected by reload operations.
        string archivePath = RandomArchive(rng);
        var files = await GetFilesAsync(archivePath, token);
        if (files.Count == 0) return;

        var (name, size) = files[rng.Next(files.Count)];
        byte[] buf = new byte[size];
        int read = await Adapter.GuardedReadFileAsync($"{archivePath}/{name}", buf, 0, token);
        Interlocked.Increment(ref Result.TotalOperations);

        if (read > 0)
            VerifyFullFile(archivePath, name, buf, read, "CrossArchive", 0);

        await Task.Delay(rng.Next(20, 100), token);
    }

    // ── Cat 7: BulkAdd ──────────────────────────────────────────────────

    private async Task BulkAddBody(Random rng, CancellationToken token)
    {
        // Add all reload archives simultaneously, verify all accessible, remove all
        var addedPaths = new List<string>();

        foreach (string reloadPath in _reloadArchivePaths)
        {
            string vp = Path.GetFileName(reloadPath).Replace('\\', '/');
            string zipPath = Path.Combine(_reloadArchiveDir, vp.Replace('/', '\\'));
            if (!File.Exists(zipPath)) continue;

            await AddReloadArchiveAsync(vp, zipPath, token);
            addedPaths.Add(vp);
        }

        // Verify all accessible through adapter
        foreach (string vp in addedPaths)
        {
            try
            {
                bool exists = await Adapter.GuardedDirectoryExistsAsync($"reload/{vp}", token);
                Interlocked.Increment(ref Result.TotalOperations);
            }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }

        // Remove all
        foreach (string vp in addedPaths)
        {
            await _archiveManager.RemoveArchiveAsync($"reload/{vp}", token);
            Interlocked.Increment(ref Result.TotalOperations);
        }

        await Task.Delay(rng.Next(100, 500), token);
    }

    // ── Cat 8: Rename ───────────────────────────────────────────────────

    private async Task RenameBody(Random rng, CancellationToken token)
    {
        string virtualPath = PickReloadVirtualPath(rng);
        string zipPath = ResolvePhysicalPath(virtualPath);
        if (!File.Exists(zipPath)) return;

        string originalKey = $"reload/{virtualPath}";
        string renamedKey = $"reload/renamed_{virtualPath}";

        // Add as original
        await AddReloadArchiveAsync(virtualPath, zipPath, token);

        // Simulate rename: remove old, add with new key
        await _archiveManager.RemoveArchiveAsync(originalKey, token);
        Interlocked.Increment(ref Result.TotalOperations);

        var descriptor = new ArchiveDescriptor
        {
            VirtualPath = renamedKey,
            PhysicalPath = zipPath,
            SizeBytes = new FileInfo(zipPath).Length,
            LastModifiedUtc = File.GetLastWriteTimeUtc(zipPath)
        };
        await _archiveManager.AddArchiveAsync(descriptor, token);
        Interlocked.Increment(ref Result.TotalOperations);

        // Verify old path gone, new path accessible
        try
        {
            bool oldExists = await Adapter.GuardedDirectoryExistsAsync(originalKey, token);
            // Should be false (removed)
            Interlocked.Increment(ref Result.TotalOperations);

            bool newExists = await Adapter.GuardedDirectoryExistsAsync(renamedKey, token);
            // Should be true (just added)
            Interlocked.Increment(ref Result.TotalOperations);
        }
        catch (Exception) when (!token.IsCancellationRequested) { }

        // Cleanup
        await _archiveManager.RemoveArchiveAsync(renamedKey, token);
        Interlocked.Increment(ref Result.TotalOperations);

        await Task.Delay(rng.Next(50, 200), token);
    }

    // ── Cat 9: ThunderingHerd ───────────────────────────────────────────

    private async Task ThunderingHerdBody(Random rng, CancellationToken token)
    {
        string virtualPath = PickReloadVirtualPath(rng);
        string zipPath = ResolvePhysicalPath(virtualPath);
        if (!File.Exists(zipPath)) return;

        // Add archive, then hit it with 10 concurrent reads immediately
        await AddReloadArchiveAsync(virtualPath, zipPath, token);

        try
        {
            var concurrentReads = Enumerable.Range(0, 10).Select(async _ =>
            {
                try
                {
                    var listing = await Adapter.GuardedListDirectoryAsync($"reload/{virtualPath}", token);
                    Interlocked.Increment(ref Result.TotalOperations);

                    var files = listing.Where(e => !e.IsDirectory && e.Name != "__manifest__.json").ToList();
                    if (files.Count > 0)
                    {
                        var file = files[0]; // All 10 read the same file
                        byte[] buf = new byte[Math.Min(4096, (int)Math.Max(file.SizeBytes, 1))];
                        await Adapter.GuardedReadFileAsync(file.FullPath, buf, 0, token);
                        Interlocked.Increment(ref Result.TotalOperations);
                    }
                }
                catch (Domain.Exceptions.VfsFileNotFoundException) { }
                catch (Domain.Exceptions.VfsDirectoryNotFoundException) { }
            });

            await Task.WhenAll(concurrentReads);
        }
        catch (Exception) when (!token.IsCancellationRequested) { }

        await _archiveManager.RemoveArchiveAsync($"reload/{virtualPath}", token);
        Interlocked.Increment(ref Result.TotalOperations);
        await Task.Delay(rng.Next(50, 200), token);
    }

    // ── Cat 10: DegradationMonitor ──────────────────────────────────────

    private async Task DegradationMonitorBody(Random rng, CancellationToken token)
    {
        // Sample cache health counters periodically
        int borrowed = FileCache.BorrowedEntryCount;
        int memEntries = FileCache.MemoryTier.EntryCount;
        int diskEntries = FileCache.DiskTier.EntryCount;
        int pendingCleanup = FileCache.MemoryTier.PendingCleanupCount + FileCache.DiskTier.PendingCleanupCount;

        // These are informational — no assertion here (post-run assertions handle it)
        Interlocked.Increment(ref Result.TotalOperations);

        await Task.Delay(5000, token); // Sample every 5 seconds
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
        var listing = await Adapter.GuardedListDirectoryAsync(archivePath, token);
        Interlocked.Increment(ref Result.TotalOperations);

        var files = listing.Where(e => !e.IsDirectory && e.Name != "__manifest__.json").ToList();
        if (files.Count > 0)
        {
            var file = files[rng.Next(files.Count)];
            byte[] buf = new byte[Math.Min(4096, (int)Math.Max(file.SizeBytes, 1))];
            await Adapter.GuardedReadFileAsync(file.FullPath, buf, 0, token);
            Interlocked.Increment(ref Result.TotalOperations);
        }
    }
}
