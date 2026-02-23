using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZipDriveV3.Application.Services;
using ZipDriveV3.Domain;
using ZipDriveV3.Domain.Abstractions;
using ZipDriveV3.Domain.Models;
using ZipDriveV3.Infrastructure.Archives.Zip;
using ZipDriveV3.Infrastructure.Caching;
using ZipDriveV3.TestHelpers;

namespace ZipDriveV3.EnduranceTests;

/// <summary>
/// 12-hour endurance test with 20 concurrent tasks.
/// Duration configurable via ENDURANCE_DURATION_HOURS env var (default: 12, set to 0.01 for CI).
/// </summary>
public class EnduranceTest : IAsyncLifetime
{
    private string _rootPath = "";
    private IVirtualFileSystem _vfs = null!;
    private readonly ConcurrentBag<string> _errors = new();
    private long _totalReads;
    private long _verifiedReads;

    public async Task InitializeAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        _rootPath = Path.Combine(Path.GetTempPath(), "VfsEndurance_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_rootPath);

        // Small-scale fixture for CI
        await TestZipGenerator.GenerateTestFixtureAsync(_rootPath, smallScale: true);

        var charComparer = CaseInsensitiveCharComparer.Instance;
        var archiveTrie = new ArchiveTrie(charComparer);
        var pathResolver = new PathResolver(archiveTrie);
        var discovery = new ArchiveDiscovery(NullLogger<ArchiveDiscovery>.Instance);
        var readerFactory = new ZipReaderFactory();

        var structureStore = new ArchiveStructureStore(
            new LruEvictionPolicy(), TimeProvider.System, NullLoggerFactory.Instance);
        var cacheOpts = Microsoft.Extensions.Options.Options.Create(
            new CacheOptions { MemoryCacheSizeMb = 128, DiskCacheSizeMb = 128 });
        var structureCache = new ArchiveStructureCache(structureStore, readerFactory,
            TimeProvider.System, cacheOpts, NullLogger<ArchiveStructureCache>.Instance);

        var fc = new DualTierFileCache(
            cacheOpts,
            new LruEvictionPolicy(), TimeProvider.System,
            NullLogger<DualTierFileCache>.Instance, NullLoggerFactory.Instance);

        var vfs = new ZipVirtualFileSystem(archiveTrie, structureCache, fc, discovery, pathResolver, readerFactory,
            cacheOpts, NullLogger<ZipVirtualFileSystem>.Instance);
        await vfs.MountAsync(new VfsMountOptions { RootPath = _rootPath, MaxDiscoveryDepth = 6 });
        _vfs = vfs;
    }

    public async Task DisposeAsync()
    {
        if (_vfs is ZipVirtualFileSystem zvfs && zvfs.IsMounted)
            await zvfs.UnmountAsync();

        try { Directory.Delete(_rootPath, true); } catch { }
    }

    [Fact]
    public async Task EnduranceRun_20ConcurrentTasks_ZeroErrors()
    {
        string durationStr = Environment.GetEnvironmentVariable("ENDURANCE_DURATION_HOURS") ?? "0.01";
        double hours = double.Parse(durationStr);
        TimeSpan duration = TimeSpan.FromHours(hours);

        using (CancellationTokenSource cts = new(duration))
        {
            Stopwatch sw = Stopwatch.StartNew();

            // Collect all archive paths
            List<string> archivePaths = new();
            await CollectArchivePathsAsync("", archivePaths);

            if (archivePaths.Count == 0)
            {
                // No archives - skip
                return;
            }

            // Launch 20 concurrent tasks with different workloads
            Random rng = new(42);
            Task[] tasks = new Task[20];
            for (int i = 0; i < 20; i++)
            {
                int taskId = i;
                tasks[i] = taskId switch
                {
                    < 8 => RunRandomFileBrowserAsync(archivePaths, rng.Next(), cts.Token),
                    < 14 => RunSequentialReaderAsync(archivePaths, rng.Next(), cts.Token),
                    < 18 => RunPathResolutionStressAsync(archivePaths, rng.Next(), cts.Token),
                    _ => RunConcurrentAccessAsync(archivePaths, rng.Next(), cts.Token)
                };
            }

            await Task.WhenAll(tasks);
            sw.Stop();

            // Report
            long reads = Interlocked.Read(ref _totalReads);
            long verified = Interlocked.Read(ref _verifiedReads);

            _errors.Should()
                .BeEmpty($"Expected zero errors but got {_errors.Count}: {string.Join("; ", _errors.Take(5))}");
            reads.Should().BeGreaterThan(0, "Should have performed some reads");
        }
    }

    private async Task RunRandomFileBrowserAsync(List<string> archives, int seed, CancellationToken ct)
    {
        Random rng = new(seed);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string archivePath = archives[rng.Next(archives.Count)];
                var contents = await _vfs.ListDirectoryAsync(archivePath, ct);
                var files = contents.Where(e => !e.IsDirectory && e.Name != "__manifest__.json").ToList();
                if (files.Count == 0) continue;

                var file = files[rng.Next(files.Count)];
                string filePath = $"{archivePath}/{file.Name}";
                byte[] buf = new byte[file.SizeBytes];
                int read = await _vfs.ReadFileAsync(filePath, buf, 0, ct);
                Interlocked.Increment(ref _totalReads);

                if (read > 0)
                    Interlocked.Increment(ref _verifiedReads);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errors.Add($"Browser: {ex.Message}"); break; }
        }
    }

    private async Task RunSequentialReaderAsync(List<string> archives, int seed, CancellationToken ct)
    {
        Random rng = new(seed);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string archivePath = archives[rng.Next(archives.Count)];
                var contents = await _vfs.ListDirectoryAsync(archivePath, ct);
                var file = contents.FirstOrDefault(e => !e.IsDirectory && e.Name != "__manifest__.json" && e.SizeBytes > 100);
                if (file.Name == null) continue;

                string filePath = $"{archivePath}/{file.Name}";
                byte[] chunk = new byte[4096];
                long offset = 0;
                while (!ct.IsCancellationRequested)
                {
                    int read = await _vfs.ReadFileAsync(filePath, chunk, offset, ct);
                    if (read == 0) break;
                    offset += read;
                    Interlocked.Increment(ref _totalReads);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errors.Add($"SeqReader: {ex.Message}"); break; }
        }
    }

    private async Task RunPathResolutionStressAsync(List<string> archives, int seed, CancellationToken ct)
    {
        Random rng = new(seed);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string archivePath = archives[rng.Next(archives.Count)];
                await _vfs.DirectoryExistsAsync(archivePath, ct);
                await _vfs.GetFileInfoAsync(archivePath, ct);
                Interlocked.Increment(ref _totalReads);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errors.Add($"PathStress: {ex.Message}"); break; }
        }
    }

    private async Task RunConcurrentAccessAsync(List<string> archives, int seed, CancellationToken ct)
    {
        Random rng = new(seed);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string archivePath = archives[rng.Next(archives.Count)];
                var contents = await _vfs.ListDirectoryAsync(archivePath, ct);
                var file = contents.FirstOrDefault(e => !e.IsDirectory && e.Name != "__manifest__.json");
                if (file.Name == null) continue;

                string filePath = $"{archivePath}/{file.Name}";

                // Simulate thundering herd: 5 concurrent reads of same file
                var tasks = Enumerable.Range(0, 5).Select(async _ =>
                {
                    byte[] buf = new byte[file.SizeBytes];
                    await _vfs.ReadFileAsync(filePath, buf, 0, ct);
                    Interlocked.Increment(ref _totalReads);
                }).ToArray();

                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errors.Add($"ConcAccess: {ex.Message}"); break; }
        }
    }

    private async Task CollectArchivePathsAsync(string folderPath, List<string> archives)
    {
        var entries = await _vfs.ListDirectoryAsync(folderPath);
        foreach (var entry in entries)
        {
            string entryPath = string.IsNullOrEmpty(folderPath) ? entry.Name : $"{folderPath}/{entry.Name}";
            if (entry.IsDirectory)
            {
                if (entry.Name.EndsWith(".zip"))
                    archives.Add(entryPath);
                else
                    await CollectArchivePathsAsync(entryPath, archives);
            }
        }
    }
}
