using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZipDrive.Application.Services;
using ZipDrive.Domain;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests;

/// <summary>
/// Endurance test with 20 concurrent tasks exercising all cache patterns:
/// - Memory tier (files &lt; 5MB) and disk tier (files &gt;= 5MB)
/// - LRU eviction under tight capacity (2MB memory, 4MB disk)
/// - TTL expiration with 1-minute TTL and 2-second maintenance interval
/// - SHA-256 content verification on every read
/// - Thundering herd (concurrent reads of same file)
/// - Post-run assertions: zero handle leaks, zero errors, verified reads
///
/// Duration configurable via ENDURANCE_DURATION_HOURS env var (default: 0.02 = ~72s for CI).
/// </summary>
public class EnduranceTest : IAsyncLifetime
{
    private string _rootPath = "";
    private ZipVirtualFileSystem _vfs = null!;
    private DualTierFileCache _fileCache = null!;
    private IArchiveStructureCache _structureCache = null!;

    private readonly ConcurrentBag<string> _errors = new();
    private long _totalReads;
    private long _verifiedReads;
    private long _contentVerifications;
    private long _evictionCycles;

    /// <summary>
    /// Pre-loaded manifests keyed by archive VFS path (e.g., "small/archive01.zip").
    /// </summary>
    private readonly ConcurrentDictionary<string, ZipManifest> _manifests = new();

    public async Task InitializeAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        _rootPath = Path.Combine(Path.GetTempPath(), "VfsEndurance_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_rootPath);

        // Generate endurance-specific fixture with files spanning both cache tiers
        await TestZipGenerator.GenerateTestFixtureAsync(
            _rootPath, TestZipGenerator.GetEnduranceFixture());

        var charComparer = CaseInsensitiveCharComparer.Instance;
        var archiveTrie = new ArchiveTrie(charComparer);
        var pathResolver = new PathResolver(archiveTrie);
        var discovery = new ArchiveDiscovery(NullLogger<ArchiveDiscovery>.Instance);
        var readerFactory = new ZipReaderFactory();

        // Tight cache to force eviction
        var cacheOpts = Microsoft.Extensions.Options.Options.Create(new CacheOptions
        {
            MemoryCacheSizeMb = 2,
            DiskCacheSizeMb = 20,
            SmallFileCutoffMb = 5,
            DefaultTtlMinutes = 1,
            EvictionCheckIntervalSeconds = 2
        });

        var structureStore = new ArchiveStructureStore(
            new LruEvictionPolicy(), TimeProvider.System, NullLoggerFactory.Instance);
        var encodingDetector = new FilenameEncodingDetector(0.5f, System.Text.Encoding.UTF8);
        _structureCache = new ArchiveStructureCache(structureStore, readerFactory,
            TimeProvider.System, cacheOpts, NullLogger<ArchiveStructureCache>.Instance, encodingDetector);

        _fileCache = new DualTierFileCache(
            cacheOpts,
            new LruEvictionPolicy(), TimeProvider.System,
            NullLogger<DualTierFileCache>.Instance, NullLoggerFactory.Instance);

        _vfs = new ZipVirtualFileSystem(archiveTrie, _structureCache, _fileCache, discovery, pathResolver,
            readerFactory, cacheOpts, NullLogger<ZipVirtualFileSystem>.Instance);
        await _vfs.MountAsync(new VfsMountOptions { RootPath = _rootPath, MaxDiscoveryDepth = 6 });
    }

    public async Task DisposeAsync()
    {
        if (_vfs.IsMounted)
            await _vfs.UnmountAsync();

        try { Directory.Delete(_rootPath, true); } catch { }
    }

    [Fact]
    public async Task EnduranceRun_AllCachePatterns_ZeroErrors()
    {
        string durationStr = Environment.GetEnvironmentVariable("ENDURANCE_DURATION_HOURS") ?? "0.02";
        double hours = double.Parse(durationStr);
        TimeSpan duration = TimeSpan.FromHours(hours);

        using CancellationTokenSource cts = new(duration);

        // Step 1: Collect archive paths and pre-load manifests
        List<string> archivePaths = new();
        await CollectArchivePathsAsync("", archivePaths);
        archivePaths.Should().NotBeEmpty("fixture should contain archives");

        await PreloadManifestsAsync(archivePaths, cts.Token);

        // Step 2: Launch 22 concurrent workload tasks + 1 maintenance task
        Random rng = new(42);
        Task[] tasks = new Task[23];
        for (int i = 0; i < 22; i++)
        {
            int taskId = i;
            tasks[i] = taskId switch
            {
                < 8 => RunVerifiedFileBrowserAsync(archivePaths, rng.Next(), cts.Token),
                < 13 => RunVerifiedSequentialReaderAsync(archivePaths, rng.Next(), cts.Token),
                < 17 => RunPathResolutionStressAsync(archivePaths, rng.Next(), cts.Token),
                < 20 => RunConcurrentSameFileAsync(archivePaths, rng.Next(), cts.Token),
                _ => RunConcurrentDifferentFilesAsync(archivePaths, rng.Next(), cts.Token)
            };
        }
        // Background maintenance: periodic eviction + cleanup
        tasks[22] = RunMaintenanceLoopAsync(cts.Token);

        await Task.WhenAll(tasks);

        // Step 3: Post-run assertions
        long reads = Interlocked.Read(ref _totalReads);
        long verified = Interlocked.Read(ref _verifiedReads);
        long contentChecks = Interlocked.Read(ref _contentVerifications);
        long evictions = Interlocked.Read(ref _evictionCycles);

        _errors.Should()
            .BeEmpty($"Expected zero errors but got {_errors.Count}: " +
                     $"{string.Join("; ", _errors.Take(10))}");

        reads.Should().BeGreaterThan(0, "should have performed reads");
        verified.Should().BeGreaterThan(0, "should have verified some reads");
        contentChecks.Should().BeGreaterThan(0, "should have verified content via SHA-256");
        evictions.Should().BeGreaterThan(0, "maintenance loop should have run");

        // Handle leak detection: all borrows must be returned
        _fileCache.BorrowedEntryCount.Should().Be(0,
            "all cache handles must be disposed — leaked handles prevent eviction");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Workload: Random file browser with SHA-256 verification
    // ═══════════════════════════════════════════════════════════════════

    private async Task RunVerifiedFileBrowserAsync(List<string> archives, int seed, CancellationToken ct)
    {
        Random rng = new(seed);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string archivePath = archives[rng.Next(archives.Count)];
                var contents = await _vfs.ListDirectoryAsync(archivePath, ct);
                var files = contents
                    .Where(e => !e.IsDirectory && e.Name != "__manifest__.json")
                    .ToList();
                if (files.Count == 0) continue;

                var file = files[rng.Next(files.Count)];
                string filePath = $"{archivePath}/{file.Name}";
                byte[] buf = new byte[file.SizeBytes];
                int read = await _vfs.ReadFileAsync(filePath, buf, 0, ct);
                Interlocked.Increment(ref _totalReads);

                if (read > 0)
                {
                    Interlocked.Increment(ref _verifiedReads);
                    VerifyContent(archivePath, file.Name, buf, read);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errors.Add($"Browser: {ex.GetType().Name}: {ex.Message}"); break; }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Workload: Sequential chunked reader with SHA-256 verification
    // ═══════════════════════════════════════════════════════════════════

    private async Task RunVerifiedSequentialReaderAsync(List<string> archives, int seed, CancellationToken ct)
    {
        Random rng = new(seed);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string archivePath = archives[rng.Next(archives.Count)];
                var contents = await _vfs.ListDirectoryAsync(archivePath, ct);
                var file = contents.FirstOrDefault(e =>
                    !e.IsDirectory && e.Name != "__manifest__.json" && e.SizeBytes > 100);
                if (file.Name == null) continue;

                string filePath = $"{archivePath}/{file.Name}";

                // Read entire file in 4KB chunks
                byte[] fullContent = new byte[file.SizeBytes];
                byte[] chunk = new byte[4096];
                long offset = 0;
                while (!ct.IsCancellationRequested)
                {
                    int read = await _vfs.ReadFileAsync(filePath, chunk, offset, ct);
                    if (read == 0) break;

                    // Accumulate into full buffer for verification
                    if (offset + read <= fullContent.Length)
                        Array.Copy(chunk, 0, fullContent, offset, read);

                    offset += read;
                    Interlocked.Increment(ref _totalReads);
                }

                if (offset > 0)
                {
                    Interlocked.Increment(ref _verifiedReads);
                    VerifyContent(archivePath, file.Name, fullContent, (int)offset);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errors.Add($"SeqReader: {ex.GetType().Name}: {ex.Message}"); break; }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Workload: Path resolution stress (structure cache)
    // ═══════════════════════════════════════════════════════════════════

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

                // Also list directory contents to stress structure cache
                var contents = await _vfs.ListDirectoryAsync(archivePath, ct);
                foreach (var entry in contents.Take(5))
                {
                    string entryPath = $"{archivePath}/{entry.Name}";
                    if (entry.IsDirectory)
                        await _vfs.DirectoryExistsAsync(entryPath, ct);
                    else
                        await _vfs.FileExistsAsync(entryPath, ct);
                }

                Interlocked.Increment(ref _totalReads);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errors.Add($"PathStress: {ex.GetType().Name}: {ex.Message}"); break; }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Workload: Thundering herd — 20 concurrent reads of SAME file
    // ═══════════════════════════════════════════════════════════════════

    private async Task RunConcurrentSameFileAsync(List<string> archives, int seed, CancellationToken ct)
    {
        Random rng = new(seed);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string archivePath = archives[rng.Next(archives.Count)];
                var contents = await _vfs.ListDirectoryAsync(archivePath, ct);
                var file = contents.FirstOrDefault(e =>
                    !e.IsDirectory && e.Name != "__manifest__.json");
                if (file.Name == null) continue;

                string filePath = $"{archivePath}/{file.Name}";

                // 20 concurrent reads of the same file — tests thundering herd prevention
                var tasks = Enumerable.Range(0, 20).Select(async _ =>
                {
                    byte[] buf = new byte[file.SizeBytes];
                    int read = await _vfs.ReadFileAsync(filePath, buf, 0, ct);
                    Interlocked.Increment(ref _totalReads);

                    if (read > 0)
                    {
                        Interlocked.Increment(ref _verifiedReads);
                        VerifyContent(archivePath, file.Name, buf, read);
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errors.Add($"SameFile: {ex.GetType().Name}: {ex.Message}"); break; }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Workload: Parallel materialization — 20 concurrent reads of DIFFERENT files
    // ═══════════════════════════════════════════════════════════════════

    private async Task RunConcurrentDifferentFilesAsync(List<string> archives, int seed, CancellationToken ct)
    {
        Random rng = new(seed);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Collect files from multiple archives for parallel reads
                List<(string archivePath, string filePath, string fileName, long size)> targets = new();

                for (int attempt = 0; attempt < 30 && targets.Count < 20; attempt++)
                {
                    string archivePath = archives[rng.Next(archives.Count)];
                    var contents = await _vfs.ListDirectoryAsync(archivePath, ct);
                    var files = contents
                        .Where(e => !e.IsDirectory && e.Name != "__manifest__.json")
                        .ToList();

                    foreach (var file in files)
                    {
                        string filePath = $"{archivePath}/{file.Name}";
                        if (targets.All(t => t.filePath != filePath))
                        {
                            targets.Add((archivePath, filePath, file.Name, file.SizeBytes));
                            if (targets.Count >= 20) break;
                        }
                    }
                }

                if (targets.Count == 0) continue;

                // 20 concurrent reads of different files — tests parallel materialization
                var tasks = targets.Select(async t =>
                {
                    byte[] buf = new byte[t.size];
                    int read = await _vfs.ReadFileAsync(t.filePath, buf, 0, ct);
                    Interlocked.Increment(ref _totalReads);

                    if (read > 0)
                    {
                        Interlocked.Increment(ref _verifiedReads);
                        VerifyContent(t.archivePath, t.fileName, buf, read);
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errors.Add($"DiffFiles: {ex.GetType().Name}: {ex.Message}"); break; }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Background maintenance: periodic eviction + cleanup
    // ═══════════════════════════════════════════════════════════════════

    private async Task RunMaintenanceLoopAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(2));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                _fileCache.EvictExpired();
                _structureCache.EvictExpired();
                _fileCache.ProcessPendingCleanup();
                Interlocked.Increment(ref _evictionCycles);
            }
            catch (Exception ex)
            {
                _errors.Add($"Maintenance: {ex.GetType().Name}: {ex.Message}");
                break;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies file content against the embedded manifest SHA-256 hash.
    /// </summary>
    private void VerifyContent(string archivePath, string fileName, byte[] data, int length)
    {
        if (!_manifests.TryGetValue(archivePath, out ZipManifest? manifest))
            return;

        ManifestEntry? entry = manifest.Entries
            .FirstOrDefault(e => !e.IsDirectory && e.FileName == fileName);

        if (entry == null || string.IsNullOrEmpty(entry.Sha256))
            return;

        string actualHash = Convert.ToHexStringLower(SHA256.HashData(data.AsSpan(0, length)));

        if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _errors.Add(
                $"SHA-256 mismatch for {archivePath}/{fileName}: " +
                $"expected={entry.Sha256[..16]}... actual={actualHash[..16]}...");
        }
        else
        {
            Interlocked.Increment(ref _contentVerifications);
        }
    }

    /// <summary>
    /// Pre-loads __manifest__.json from each archive via VFS reads.
    /// </summary>
    private async Task PreloadManifestsAsync(List<string> archivePaths, CancellationToken ct)
    {
        foreach (string archivePath in archivePaths)
        {
            try
            {
                var contents = await _vfs.ListDirectoryAsync(archivePath, ct);
                var manifestFile = contents.FirstOrDefault(e => e.Name == "__manifest__.json");
                if (manifestFile.Name == null) continue;

                string manifestPath = $"{archivePath}/__manifest__.json";
                byte[] buf = new byte[manifestFile.SizeBytes + 1024]; // Extra margin
                int read = await _vfs.ReadFileAsync(manifestPath, buf, 0, ct);

                if (read > 0)
                {
                    string json = Encoding.UTF8.GetString(buf, 0, read);
                    ZipManifest? manifest = JsonSerializer.Deserialize<ZipManifest>(json);
                    if (manifest != null)
                        _manifests[archivePath] = manifest;
                }
            }
            catch
            {
                // Non-fatal: skip manifests that can't be loaded
            }
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
