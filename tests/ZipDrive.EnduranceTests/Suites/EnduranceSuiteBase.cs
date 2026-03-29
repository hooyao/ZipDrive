using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Xunit.Abstractions;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.Infrastructure.FileSystem;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests.Suites;

/// <summary>
/// Base class for endurance test suites. Provides fail-fast reporting,
/// SHA-256 verification, and common file selection helpers.
/// </summary>
public abstract class EnduranceSuiteBase : IEnduranceSuite
{
    protected readonly DokanFileSystemAdapter Adapter;
    protected readonly ConcurrentDictionary<string, ZipManifest> Manifests;
    protected readonly List<string> ArchivePaths;
    protected readonly FileContentCache FileCache;
    protected readonly IArchiveStructureCache StructureCache;
    protected readonly Action<EnduranceFailure> ReportFailure;
    protected readonly Stopwatch RunStopwatch;
    protected readonly SuiteResult Result = new();

    public abstract string Name { get; }
    public abstract int TaskCount { get; }

    protected EnduranceSuiteBase(
        DokanFileSystemAdapter adapter,
        ConcurrentDictionary<string, ZipManifest> manifests,
        List<string> archivePaths,
        FileContentCache fileCache,
        IArchiveStructureCache structureCache,
        Action<EnduranceFailure> reportFailure,
        Stopwatch runStopwatch)
    {
        Adapter = adapter;
        Manifests = manifests;
        ArchivePaths = archivePaths;
        FileCache = fileCache;
        StructureCache = structureCache;
        ReportFailure = reportFailure;
        RunStopwatch = runStopwatch;
    }

    public abstract Task RunAsync(CancellationToken ct);

    public SuiteResult GetResult() => Result;

    public virtual void PrintReport(ITestOutputHelper output)
    {
        output.WriteLine($"  {Name,-30} {Interlocked.Read(ref Result.TotalOperations),8} ops, " +
                         $"{Interlocked.Read(ref Result.VerifiedOperations),6} verified, " +
                         $"{Result.Errors.Count} errors");
    }

    /// <summary>
    /// Runs a workload task in a loop with fail-fast error handling.
    /// </summary>
    protected async Task RunWorkloadLoopAsync(
        string workload, int taskId, CancellationToken ct, Func<Random, CancellationToken, Task> body)
    {
        Random rng = new(taskId * 31 + workload.GetHashCode(StringComparison.Ordinal));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await body(rng, ct);
                // Yield to prevent thread-pool starvation from hot loops
                await Task.Yield();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                ReportFailure(new EnduranceFailure
                {
                    Suite = Name,
                    TaskId = taskId,
                    Workload = workload,
                    Elapsed = RunStopwatch.Elapsed,
                    Exception = ex,
                    CacheMemoryEntries = FileCache.MemoryTier.EntryCount,
                    CacheDiskEntries = FileCache.DiskTier.EntryCount,
                    CacheBorrowedCount = FileCache.BorrowedEntryCount,
                    CachePendingCleanup = FileCache.MemoryTier.PendingCleanupCount +
                                          FileCache.DiskTier.PendingCleanupCount
                });
                return;
            }
        }
    }

    /// <summary>
    /// Verifies full-file SHA-256 against manifest. Reports failure on mismatch.
    /// </summary>
    protected void VerifyFullFile(
        string archivePath, string fileName, byte[] data, int length,
        string workload, int taskId)
    {
        if (!Manifests.TryGetValue(archivePath, out ZipManifest? manifest)) return;

        ManifestEntry? entry = manifest.Entries
            .FirstOrDefault(e => !e.IsDirectory && e.FileName == fileName);
        if (entry == null || string.IsNullOrEmpty(entry.Sha256)) return;

        string actual = Convert.ToHexStringLower(SHA256.HashData(data.AsSpan(0, length)));
        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            ReportFailure(new EnduranceFailure
            {
                Suite = Name,
                TaskId = taskId,
                Workload = workload,
                Elapsed = RunStopwatch.Elapsed,
                FilePath = $"{archivePath}/{fileName}",
                Operation = $"ReadFileAsync(offset=0, length={length})",
                ExpectedHash = entry.Sha256,
                ActualHash = actual,
                SampleDescription = "full-file",
                CacheMemoryEntries = FileCache.MemoryTier.EntryCount,
                CacheDiskEntries = FileCache.DiskTier.EntryCount,
                CacheBorrowedCount = FileCache.BorrowedEntryCount,
                CachePendingCleanup = FileCache.MemoryTier.PendingCleanupCount +
                                      FileCache.DiskTier.PendingCleanupCount
            });
            return;
        }

        Interlocked.Increment(ref Result.VerifiedOperations);
    }

    /// <summary>
    /// Verifies a partial read against a matching partial sample. Reports failure on mismatch.
    /// </summary>
    protected void VerifyPartialRead(
        string archivePath, string fileName, byte[] data, int length,
        long offset, string workload, int taskId)
    {
        if (!Manifests.TryGetValue(archivePath, out ZipManifest? manifest)) return;

        PartialSample? sample = manifest.PartialSamples
            .FirstOrDefault(s => s.FileName == fileName && s.Offset == offset && s.Length == length);
        if (sample == null) return;

        string actual = Convert.ToHexStringLower(SHA256.HashData(data.AsSpan(0, length)));
        if (!string.Equals(actual, sample.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            ReportFailure(new EnduranceFailure
            {
                Suite = Name,
                TaskId = taskId,
                Workload = workload,
                Elapsed = RunStopwatch.Elapsed,
                FilePath = $"{archivePath}/{fileName}",
                Operation = $"ReadFileAsync(offset={offset}, length={length})",
                ExpectedHash = sample.Sha256,
                ActualHash = actual,
                SampleDescription = $"partial at offset={offset}, length={length}",
                CacheMemoryEntries = FileCache.MemoryTier.EntryCount,
                CacheDiskEntries = FileCache.DiskTier.EntryCount,
                CacheBorrowedCount = FileCache.BorrowedEntryCount,
                CachePendingCleanup = FileCache.MemoryTier.PendingCleanupCount +
                                      FileCache.DiskTier.PendingCleanupCount
            });
            return;
        }

        Interlocked.Increment(ref Result.VerifiedOperations);
    }

    /// <summary>
    /// Gets non-manifest, non-directory file entries from an archive.
    /// </summary>
    protected async Task<List<(string name, long size)>> GetFilesAsync(
        string archivePath, CancellationToken ct)
    {
        var contents = await Adapter.GuardedListDirectoryAsync(archivePath, ct);
        return contents
            .Where(e => !e.IsDirectory && e.Name != "__manifest__.json")
            .Select(e => (e.Name, e.SizeBytes))
            .ToList();
    }

    protected string RandomArchive(Random rng) => ArchivePaths[rng.Next(ArchivePaths.Count)];
}
