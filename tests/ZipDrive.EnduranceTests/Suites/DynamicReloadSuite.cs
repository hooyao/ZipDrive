using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZipDrive.Application.Services;
using ZipDrive.Domain;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.Infrastructure.FileSystem;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests.Suites;

/// <summary>
/// Endurance suite that exercises the dynamic reload pattern through the shared
/// <see cref="DokanFileSystemAdapter"/>. Periodically builds a new VFS, calls
/// <see cref="DokanFileSystemAdapter.SwapAsync"/> to trigger real drain, then reads/verifies
/// files through the adapter's guarded methods while other suites are concurrently
/// reading through the same adapter.
/// </summary>
public sealed class DynamicReloadSuite : IEnduranceSuite
{
    private readonly DokanFileSystemAdapter _adapter;
    private readonly string _rootPath;
    private readonly ConcurrentDictionary<string, ZipManifest> _manifests;
    private readonly Action<EnduranceFailure> _reportFailure;
    private readonly Stopwatch _runStopwatch;
    private readonly SuiteResult _result = new();

    public string Name => "DynamicReloadSuite";
    public int TaskCount => 2;

    public DynamicReloadSuite(
        DokanFileSystemAdapter adapter,
        string rootPath,
        ConcurrentDictionary<string, ZipManifest> manifests,
        Action<EnduranceFailure> reportFailure,
        Stopwatch runStopwatch)
    {
        _adapter = adapter;
        _rootPath = rootPath;
        _manifests = manifests;
        _reportFailure = reportFailure;
        _runStopwatch = runStopwatch;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var tasks = new Task[TaskCount];
        tasks[0] = RunReloadLoopAsync("ReloadAndVerify", 0, ct);
        tasks[1] = RunReloadLoopAsync("ReloadAndVerify", 1, ct);
        await Task.WhenAll(tasks);
    }

    public SuiteResult GetResult() => _result;

    public void PrintReport(ITestOutputHelper output)
    {
        output.WriteLine($"  {Name,-30} {Interlocked.Read(ref _result.TotalOperations),8} ops, " +
                         $"{Interlocked.Read(ref _result.VerifiedOperations),6} verified, " +
                         $"{_result.Errors.Count} errors");
    }

    private async Task RunReloadLoopAsync(string workload, int taskId, CancellationToken ct)
    {
        // Track the VFS+cache that this task swapped in, so we can clean it when swapped out next.
        FileContentCache? previousCache = null;

        while (!ct.IsCancellationRequested)
        {
            IVirtualFileSystem? newVfs = null;
            FileContentCache? newFileCache = null;
            try
            {
                // Build a fresh VFS instance
                var (builtVfs, builtFileCache) = BuildVfs();
                newVfs = builtVfs;
                newFileCache = builtFileCache;

                await newVfs.MountAsync(new VfsMountOptions
                {
                    RootPath = _rootPath,
                    MaxDiscoveryDepth = 6
                }, ct);

                // Swap through the shared adapter — triggers real drain while other suites are reading
                var oldVfs = await _adapter.SwapAsync(newVfs, TimeSpan.FromSeconds(5));

                // Clean up the previous iteration's cache (disk files)
                if (previousCache != null)
                {
                    try { previousCache.Clear(); } catch { }
                    try { previousCache.DeleteCacheDirectory(); } catch { }
                }

                // Unmount the old VFS (which was either the initial or previous reload's VFS)
                if (oldVfs is { IsMounted: true })
                    try { await oldVfs.UnmountAsync(ct); } catch { }

                // Track this iteration's cache for cleanup on next swap
                previousCache = newFileCache;

                // Read and verify a file through the adapter's guarded path
                var archivePaths = await CollectArchivePathsAsync(ct);
                if (archivePaths.Count > 0)
                {
                    var rng = new Random(taskId * 31 + Environment.TickCount);
                    string archivePath = archivePaths[rng.Next(archivePaths.Count)];

                    var files = await GetFilesAsync(archivePath, ct);
                    if (files.Count > 0)
                    {
                        var (fileName, fileSize) = files[rng.Next(files.Count)];
                        string filePath = $"{archivePath}/{fileName}";

                        byte[] buffer = new byte[fileSize + 1024];
                        int bytesRead = await _adapter.GuardedReadFileAsync(filePath, buffer, 0, ct);
                        Interlocked.Increment(ref _result.TotalOperations);

                        if (bytesRead > 0 && _manifests.TryGetValue(archivePath, out var manifest))
                        {
                            var entry = manifest.Entries.FirstOrDefault(e => !e.IsDirectory && e.FileName == fileName);
                            if (entry != null && !string.IsNullOrEmpty(entry.Sha256))
                            {
                                string actual = Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, bytesRead)));
                                if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                                {
                                    _reportFailure(new EnduranceFailure
                                    {
                                        Suite = Name,
                                        TaskId = taskId,
                                        Workload = workload,
                                        Elapsed = _runStopwatch.Elapsed,
                                        FilePath = filePath,
                                        Operation = $"ReadFileAsync(offset=0, length={bytesRead})",
                                        ExpectedHash = entry.Sha256,
                                        ActualHash = actual,
                                        SampleDescription = "full-file after reload via adapter"
                                    });
                                    return;
                                }
                                Interlocked.Increment(ref _result.VerifiedOperations);
                            }
                        }
                    }
                }

                // Swap succeeded — newVfs is now active, don't dispose in finally
                newVfs = null;
                newFileCache = null;

                // Pace reload cycles to avoid filling disk
                await Task.Delay(3000, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _reportFailure(new EnduranceFailure
                {
                    Suite = Name,
                    TaskId = taskId,
                    Workload = workload,
                    Elapsed = _runStopwatch.Elapsed,
                    Exception = ex
                });
                return;
            }
            finally
            {
                // Clean up only if swap didn't complete (newVfs still ours)
                if (newVfs is { IsMounted: true })
                    try { await newVfs.UnmountAsync(); } catch { }
                if (newFileCache != null)
                {
                    try { newFileCache.Clear(); } catch { }
                    try { newFileCache.DeleteCacheDirectory(); } catch { }
                }
            }
        }

        // Final cleanup of the last swapped-in cache
        if (previousCache != null)
        {
            try { previousCache.Clear(); } catch { }
            try { previousCache.DeleteCacheDirectory(); } catch { }
        }
    }

    private (IVirtualFileSystem vfs, FileContentCache fileCache) BuildVfs()
    {
        var charComparer = CaseInsensitiveCharComparer.Instance;
        var archiveTrie = new ArchiveTrie(charComparer);
        var pathResolver = new PathResolver(archiveTrie);
        var discovery = new ArchiveDiscovery(NullLogger<ArchiveDiscovery>.Instance);
        var readerFactory = new ZipReaderFactory();

        var cacheOpts = Options.Create(new CacheOptions
        {
            MemoryCacheSizeMb = 1,
            DiskCacheSizeMb = 10,
            SmallFileCutoffMb = 1,
            ChunkSizeMb = 1,
            DefaultTtlMinutes = 1,
            EvictionCheckIntervalSeconds = 60
        });

        var structureStore = new ArchiveStructureStore(
            new LruEvictionPolicy(), TimeProvider.System, NullLoggerFactory.Instance);
        var encodingDetector = new FilenameEncodingDetector(
            Options.Create(new MountSettings()),
            NullLogger<FilenameEncodingDetector>.Instance);
        var structureCache = new ArchiveStructureCache(structureStore, readerFactory,
            TimeProvider.System, cacheOpts, NullLogger<ArchiveStructureCache>.Instance, encodingDetector);

        var fileCache = new FileContentCache(
            readerFactory, cacheOpts,
            new LruEvictionPolicy(), TimeProvider.System,
            NullLoggerFactory.Instance);

        var vfs = new ZipVirtualFileSystem(archiveTrie, structureCache, fileCache, discovery, pathResolver,
            new NullHostApplicationLifetime(),
            Options.Create(new PrefetchOptions { Enabled = false }),
            NullLogger<ZipVirtualFileSystem>.Instance);

        return (vfs, fileCache);
    }

    private async Task<List<string>> CollectArchivePathsAsync(CancellationToken ct)
    {
        var archives = new List<string>();
        await CollectArchivesRecursiveAsync("", archives, ct);
        return archives;
    }

    private async Task CollectArchivesRecursiveAsync(
        string folderPath, List<string> archives, CancellationToken ct)
    {
        var entries = await _adapter.GuardedListDirectoryAsync(folderPath, ct);
        foreach (var entry in entries)
        {
            string entryPath = string.IsNullOrEmpty(folderPath) ? entry.Name : $"{folderPath}/{entry.Name}";
            if (entry.IsDirectory)
            {
                if (entry.Name.EndsWith(".zip"))
                    archives.Add(entryPath);
                else
                    await CollectArchivesRecursiveAsync(entryPath, archives, ct);
            }
        }
    }

    private async Task<List<(string name, long size)>> GetFilesAsync(
        string archivePath, CancellationToken ct)
    {
        var contents = await _adapter.GuardedListDirectoryAsync(archivePath, ct);
        return contents
            .Where(e => !e.IsDirectory && e.Name != "__manifest__.json")
            .Select(e => (e.Name, e.SizeBytes))
            .ToList();
    }
}
