using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;
using ZipDrive.Application.Services;
using ZipDrive.Domain;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Models;
using ZipDrive.EnduranceTests.Suites;
using ZipDrive.Infrastructure.Archives.Zip;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests;

/// <summary>
/// Endurance test with 100 concurrent tasks across 7 virtual suites exercising all cache patterns:
/// - Memory tier (files &lt; 1MB) and disk tier (files &gt;= 1MB)
/// - LRU eviction under tight capacity (1MB memory, 10MB disk)
/// - TTL expiration with 1-minute TTL and 2-second maintenance interval
/// - SHA-256 verification on full-file and partial reads
/// - Thundering herd, parallel materialization, edge cases
/// - Fail-fast: first error stops all 100 tasks with rich diagnostics
/// - Latency measurement with p50/p95/p99/max reporting (informational)
///
/// Duration configurable via ENDURANCE_DURATION_HOURS env var (default: 0.02 = ~72s for CI).
/// For manual 24-hour runs: ENDURANCE_DURATION_HOURS=24
/// </summary>
public class EnduranceTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    private string _rootPath = "";
    private ZipVirtualFileSystem _vfs = null!;
    private FileContentCache _fileCache = null!;
    private IArchiveStructureCache _structureCache = null!;
    private double _durationHours;

    private readonly ConcurrentDictionary<string, ZipManifest> _manifests = new();

    // Fail-fast infrastructure
    private EnduranceFailure? _firstError;
    private CancellationTokenSource _failFastCts = null!;
    private readonly Stopwatch _runStopwatch = new();
    private long _evictionCycles;

    public EnduranceTest(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        string durationStr = Environment.GetEnvironmentVariable("ENDURANCE_DURATION_HOURS") ?? "0.02";
        _durationHours = double.Parse(durationStr);

        _rootPath = Path.Combine(Path.GetTempPath(), "VfsEndurance_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_rootPath);

        // Duration-aware fixture: small for CI (< 1h), full for manual (>= 1h)
        var fixture = _durationHours >= 1.0
            ? TestZipGenerator.GetEnduranceFullFixture()
            : TestZipGenerator.GetEnduranceFixture();
        await TestZipGenerator.GenerateTestFixtureAsync(_rootPath, fixture);

        var charComparer = CaseInsensitiveCharComparer.Instance;
        var archiveTrie = new ArchiveTrie(charComparer);
        var pathResolver = new PathResolver(archiveTrie);
        var discovery = new ArchiveDiscovery(NullLogger<ArchiveDiscovery>.Instance);
        var readerFactory = new ZipReaderFactory();

        // Tight cache to force constant eviction
        var cacheOpts = Microsoft.Extensions.Options.Options.Create(new CacheOptions
        {
            MemoryCacheSizeMb = 1,
            DiskCacheSizeMb = 10,
            SmallFileCutoffMb = 1,
            ChunkSizeMb = 1,
            DefaultTtlMinutes = 1,
            EvictionCheckIntervalSeconds = 2
        });

        var structureStore = new ArchiveStructureStore(
            new LruEvictionPolicy(), TimeProvider.System, NullLoggerFactory.Instance);
        var encodingDetector = new FilenameEncodingDetector(
            Microsoft.Extensions.Options.Options.Create(new MountSettings()),
            NullLogger<FilenameEncodingDetector>.Instance);
        _structureCache = new ArchiveStructureCache(structureStore, readerFactory,
            TimeProvider.System, cacheOpts, NullLogger<ArchiveStructureCache>.Instance, encodingDetector);

        _fileCache = new FileContentCache(
            readerFactory, cacheOpts,
            new LruEvictionPolicy(), TimeProvider.System,
            NullLoggerFactory.Instance);

        _vfs = new ZipVirtualFileSystem(archiveTrie, _structureCache, _fileCache, discovery, pathResolver,
            new NullHostApplicationLifetime(),
            Options.Create(new PrefetchOptions { Enabled = false }),
            NullLogger<ZipVirtualFileSystem>.Instance);
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
        TimeSpan duration = TimeSpan.FromHours(_durationHours);
        using CancellationTokenSource durationCts = new(duration);
        _failFastCts = CancellationTokenSource.CreateLinkedTokenSource(durationCts.Token);

        // Step 1: Collect archive paths and pre-load manifests
        List<string> archivePaths = new();
        await CollectArchivePathsAsync("", archivePaths);
        archivePaths.Should().NotBeEmpty("fixture should contain archives");

        await PreloadManifestsAsync(archivePaths, _failFastCts.Token);

        _output.WriteLine($"Endurance test starting: {archivePaths.Count} archives, " +
                           $"duration={duration}, manifests={_manifests.Count}");

        // Step 2: Create all suites
        var latencyRecorder = new LatencyRecorder();
        var suites = new IEnduranceSuite[]
        {
            new NormalReadSuite(_vfs, _manifests, archivePaths, _fileCache, _structureCache,
                ReportFailure, _runStopwatch),
            new PartialReadSuite(_vfs, _manifests, archivePaths, _fileCache, _structureCache,
                ReportFailure, _runStopwatch),
            new ConcurrencyStressSuite(_vfs, _manifests, archivePaths, _fileCache, _structureCache,
                ReportFailure, _runStopwatch),
            new EdgeCaseSuite(_vfs, _manifests, archivePaths, _fileCache, _structureCache,
                ReportFailure, _runStopwatch),
            new EvictionValidationSuite(_vfs, _manifests, archivePaths, _fileCache, _structureCache,
                ReportFailure, _runStopwatch),
            new PathResolutionSuite(_vfs, _manifests, archivePaths, _fileCache, _structureCache,
                ReportFailure, _runStopwatch),
            new LatencyMeasurementSuite(_vfs, _manifests, archivePaths, _fileCache, _structureCache,
                ReportFailure, _runStopwatch, latencyRecorder),
        };

        // Step 3: Launch all suite tasks + 2 maintenance tasks = 100 total
        _runStopwatch.Start();

        var allTasks = new List<Task>();
        foreach (var suite in suites)
            allTasks.Add(suite.RunAsync(_failFastCts.Token));
        allTasks.Add(RunMaintenanceLoopAsync(_failFastCts.Token));
        allTasks.Add(RunMaintenanceLoopAsync(_failFastCts.Token));

        await Task.WhenAll(allTasks);
        _runStopwatch.Stop();

        // Step 4: Graceful drain — wait for handles to be released
        if (_firstError != null)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5)); } catch { }
        }

        // Step 5: Report results
        _output.WriteLine("");
        _output.WriteLine("═══════════════════════════════════════════════════════════════");
        _output.WriteLine("  Endurance Test Results");
        _output.WriteLine("═══════════════════════════════════════════════════════════════");
        _output.WriteLine($"  Duration: {_runStopwatch.Elapsed:hh\\:mm\\:ss\\.fff}");
        _output.WriteLine($"  Maintenance cycles: {Interlocked.Read(ref _evictionCycles)}");
        _output.WriteLine("");

        foreach (var suite in suites)
            suite.PrintReport(_output);

        _output.WriteLine("");

        // Latency report (informational only)
        latencyRecorder.PrintReport(_output);

        // Step 6: Assertions
        if (_firstError != null)
        {
            _output.WriteLine(_firstError.FormatDiagnostic());
            Assert.Fail($"Endurance test failed: {_firstError.FormatDiagnostic()}");
        }

        // Per-suite operation checks
        foreach (var suite in suites)
        {
            var result = suite.GetResult();
            result.Errors.Should().BeEmpty(
                $"{suite.Name} should have zero errors but got: {string.Join("; ", result.Errors.Take(3))}");
            Interlocked.Read(ref result.TotalOperations).Should().BeGreaterThan(0,
                $"{suite.Name} should have performed operations");
        }

        Interlocked.Read(ref _evictionCycles).Should().BeGreaterThan(0,
            "maintenance loop should have run");

        // Handle leak detection
        _fileCache.BorrowedEntryCount.Should().Be(0,
            "all cache handles must be disposed — leaked handles prevent eviction");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Fail-fast
    // ═══════════════════════════════════════════════════════════════════

    private void ReportFailure(EnduranceFailure failure)
    {
        if (Interlocked.CompareExchange(ref _firstError, failure, null) == null)
        {
            _failFastCts.Cancel();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Maintenance (2 tasks on dedicated threads)
    // ═══════════════════════════════════════════════════════════════════

    private Task RunMaintenanceLoopAsync(CancellationToken ct)
    {
        TaskCompletionSource maintenanceDone = new();
        Thread maintenanceThread = new(() =>
        {
            Interlocked.Increment(ref _evictionCycles);
            while (!ct.IsCancellationRequested)
            {
                Thread.Sleep(2000);
                if (ct.IsCancellationRequested) break;

                Interlocked.Increment(ref _evictionCycles);

                try
                {
                    _fileCache.EvictExpired();
                    _structureCache.EvictExpired();
                    _fileCache.ProcessPendingCleanup();
                }
                catch (Exception ex)
                {
                    ReportFailure(new EnduranceFailure
                    {
                        Suite = "Maintenance",
                        TaskId = 0,
                        Workload = "EvictAndCleanup",
                        Elapsed = _runStopwatch.Elapsed,
                        Exception = ex,
                        CacheMemoryEntries = _fileCache.MemoryTier.EntryCount,
                        CacheDiskEntries = _fileCache.DiskTier.EntryCount,
                        CacheBorrowedCount = _fileCache.BorrowedEntryCount,
                        CachePendingCleanup = _fileCache.MemoryTier.PendingCleanupCount +
                                              _fileCache.DiskTier.PendingCleanupCount
                    });
                    break;
                }
            }
            maintenanceDone.SetResult();
        })
        {
            IsBackground = true,
            Name = "EnduranceMaintenance"
        };
        maintenanceThread.Start();
        return maintenanceDone.Task;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

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
                byte[] buf = new byte[manifestFile.SizeBytes + 1024];
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
