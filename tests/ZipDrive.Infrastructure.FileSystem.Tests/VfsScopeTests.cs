using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.FileSystem;

namespace ZipDrive.Infrastructure.FileSystem.Tests;

public class VfsScopeTests
{
    /// <summary>Minimal fake VFS for testing VfsScope lifecycle.</summary>
    private sealed class FakeVfs : IVirtualFileSystem
    {
        public bool IsMounted { get; set; } = true;
        public bool UnmountCalled { get; private set; }
        public event EventHandler<bool>? MountStateChanged;

        public Task MountAsync(VfsMountOptions options, CancellationToken ct = default)
        {
            IsMounted = true;
            MountStateChanged?.Invoke(this, true);
            return Task.CompletedTask;
        }

        public Task UnmountAsync(CancellationToken ct = default)
        {
            UnmountCalled = true;
            IsMounted = false;
            return Task.CompletedTask;
        }

        public Task<VfsFileInfo> GetFileInfoAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<VfsFileInfo>> ListDirectoryAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ReadFileAsync(string path, byte[] buffer, long offset, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> FileExistsAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public VfsVolumeInfo GetVolumeInfo() => throw new NotImplementedException();
    }

    /// <summary>Minimal fake content cache tracking calls.</summary>
    private sealed class FakeFileContentCache : IFileContentCache
    {
        public bool ClearCalled { get; private set; }
        public bool DeleteCacheDirectoryCalled { get; private set; }
        public int EvictExpiredCount { get; private set; }
        public int ProcessPendingCleanupCount { get; private set; }

        public void EvictExpired() => EvictExpiredCount++;
        public void Clear() => ClearCalled = true;
        public int ProcessPendingCleanup(int maxItems = 100) { ProcessPendingCleanupCount++; return 0; }
        public void DeleteCacheDirectory() => DeleteCacheDirectoryCalled = true;

        public Task<int> ReadAsync(string archivePath, ZipDrive.Domain.Models.ZipEntryInfo entry, string cacheKey, byte[] buffer, long offset, CancellationToken ct = default) => throw new NotImplementedException();
        public Task WarmAsync(ZipDrive.Domain.Models.ZipEntryInfo entry, string cacheKey, Stream decompressedStream, CancellationToken ct = default) => throw new NotImplementedException();
        public bool ContainsKey(string cacheKey) => false;
        public long CurrentSizeBytes => 0;
        public long CapacityBytes => 0;
        public double HitRate => 0;
        public int EntryCount => 0;
        public int BorrowedEntryCount => 0;
    }

    /// <summary>Minimal fake structure cache tracking calls.</summary>
    private sealed class FakeStructureCache : IArchiveStructureCache
    {
        public int EvictExpiredCount { get; private set; }
        public bool ClearCalled { get; private set; }

        public void EvictExpired() => EvictExpiredCount++;
        public void Clear() => ClearCalled = true;

        public Task<ZipDrive.Domain.Models.ArchiveStructure> GetOrBuildAsync(string archiveKey, string absolutePath, CancellationToken ct = default) => throw new NotImplementedException();
        public bool Invalidate(string archiveKey) => false;
        public int CachedArchiveCount => 0;
        public long EstimatedMemoryBytes => 0;
        public double HitRate => 0;
        public long HitCount => 0;
        public long MissCount => 0;
    }

    /// <summary>Fake IServiceScope that tracks disposal.</summary>
    private sealed class FakeServiceScope : IServiceScope
    {
        public bool Disposed { get; private set; }
        public IServiceProvider ServiceProvider => throw new NotImplementedException();
        public void Dispose() => Disposed = true;
    }

    private static VfsScope CreateTestScope(
        FakeVfs? vfs = null,
        FakeFileContentCache? fileCache = null,
        FakeStructureCache? structureCache = null,
        FakeServiceScope? scope = null,
        TimeSpan? maintenanceInterval = null)
    {
        return new VfsScope(
            scope ?? new FakeServiceScope(),
            vfs ?? new FakeVfs(),
            fileCache ?? new FakeFileContentCache(),
            structureCache ?? new FakeStructureCache(),
            maintenanceInterval ?? TimeSpan.FromHours(1), // long interval so it doesn't fire during tests
            NullLogger<VfsScope>.Instance);
    }

    [Fact]
    public async Task Vfs_property_returns_injected_instance()
    {
        var vfs = new FakeVfs();
        await using var scope = CreateTestScope(vfs: vfs);
        scope.Vfs.Should().BeSameAs(vfs);
    }

    [Fact]
    public async Task DisposeAsync_unmounts_vfs()
    {
        var vfs = new FakeVfs();
        var scope = CreateTestScope(vfs: vfs);

        await scope.DisposeAsync();

        vfs.UnmountCalled.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_clears_file_cache_and_deletes_directory()
    {
        var fileCache = new FakeFileContentCache();
        var scope = CreateTestScope(fileCache: fileCache);

        await scope.DisposeAsync();

        fileCache.ClearCalled.Should().BeTrue();
        fileCache.DeleteCacheDirectoryCalled.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_disposes_di_scope()
    {
        var diScope = new FakeServiceScope();
        var scope = CreateTestScope(scope: diScope);

        await scope.DisposeAsync();

        diScope.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent()
    {
        var vfs = new FakeVfs();
        var diScope = new FakeServiceScope();
        var scope = CreateTestScope(vfs: vfs, scope: diScope);

        await scope.DisposeAsync();
        await scope.DisposeAsync(); // should not throw

        vfs.UnmountCalled.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_skips_unmount_if_not_mounted()
    {
        var vfs = new FakeVfs { IsMounted = false };
        var scope = CreateTestScope(vfs: vfs);

        await scope.DisposeAsync();

        vfs.UnmountCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Maintenance_runs_on_timer_tick()
    {
        var fileCache = new FakeFileContentCache();
        var structureCache = new FakeStructureCache();

        // Use a very short interval so we can observe ticks
        var scope = CreateTestScope(
            fileCache: fileCache,
            structureCache: structureCache,
            maintenanceInterval: TimeSpan.FromMilliseconds(50));

        // Wait for at least one tick
        await Task.Delay(200);

        await scope.DisposeAsync();

        fileCache.EvictExpiredCount.Should().BeGreaterThan(0);
        structureCache.EvictExpiredCount.Should().BeGreaterThan(0);
        fileCache.ProcessPendingCleanupCount.Should().BeGreaterThan(0);
    }
}
