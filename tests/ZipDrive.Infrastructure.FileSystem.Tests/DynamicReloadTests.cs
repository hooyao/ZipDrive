using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

namespace ZipDrive.Infrastructure.FileSystem.Tests;

/// <summary>
/// Integration tests for the dynamic reload lifecycle:
/// VfsScope creation, mount, swap, and dispose with real VFS instances.
/// </summary>
public class DynamicReloadTests : IAsyncLifetime
{
    private string _rootDir = null!;
    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), $"DynReloadTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDir);

        // Create a minimal ZIP for testing
        await CreateTestZipAsync(Path.Combine(_rootDir, "test1.zip"), "file1.txt", "Hello from test1");

        // Build DI container matching Program.cs pattern
        var services = new ServiceCollection();
        services.Configure<MountSettings>(o => { o.ArchiveDirectory = _rootDir; o.MaxDiscoveryDepth = 1; });
        services.Configure<CacheOptions>(o => { o.MemoryCacheSizeMb = 64; o.DiskCacheSizeMb = 64; });
        services.Configure<PrefetchOptions>(_ => { });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IZipReaderFactory, ZipReaderFactory>();
        services.AddSingleton<IEvictionPolicy, LruEvictionPolicy>();
        services.AddSingleton<IFilenameEncodingDetector, FilenameEncodingDetector>();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IHostApplicationLifetime, NullHostApplicationLifetime>();

        IEqualityComparer<char>? charComparer = OperatingSystem.IsWindows()
            ? CaseInsensitiveCharComparer.Instance : null;
        services.AddScoped<IArchiveTrie>(_ => new ArchiveTrie(charComparer));
        services.AddScoped<IPathResolver, PathResolver>();
        services.AddScoped<IArchiveDiscovery, ArchiveDiscovery>();
        services.AddScoped<IArchiveStructureStore, ArchiveStructureStore>();
        services.AddScoped<IArchiveStructureCache, ArchiveStructureCache>();
        services.AddScoped<IFileContentCache, FileContentCache>();
        services.AddScoped<IVirtualFileSystem, ZipVirtualFileSystem>();

        _serviceProvider = services.BuildServiceProvider();
    }

    public Task DisposeAsync()
    {
        _serviceProvider?.Dispose();
        try { if (Directory.Exists(_rootDir)) Directory.Delete(_rootDir, true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AddZip_NewArchiveAppearsAfterReload()
    {
        // Initial scope sees test1.zip
        var scope1 = VfsScope.Create(_serviceProvider, NullLogger<VfsScope>.Instance);
        await scope1.Vfs.MountAsync(new VfsMountOptions { RootPath = _rootDir, MaxDiscoveryDepth = 1 });

        var entries1 = await scope1.Vfs.ListDirectoryAsync("\\");
        entries1.Select(e => e.Name).Should().Contain("test1.zip");
        entries1.Select(e => e.Name).Should().NotContain("test2.zip");

        // Add a new ZIP
        await CreateTestZipAsync(Path.Combine(_rootDir, "test2.zip"), "file2.txt", "Hello from test2");

        // New scope picks up both
        var scope2 = VfsScope.Create(_serviceProvider, NullLogger<VfsScope>.Instance);
        await scope2.Vfs.MountAsync(new VfsMountOptions { RootPath = _rootDir, MaxDiscoveryDepth = 1 });

        var entries2 = await scope2.Vfs.ListDirectoryAsync("\\");
        entries2.Select(e => e.Name).Should().Contain("test1.zip");
        entries2.Select(e => e.Name).Should().Contain("test2.zip");

        await scope1.DisposeAsync();
        await scope2.DisposeAsync();
    }

    [Fact]
    public async Task RemoveZip_ArchiveDisappearsAfterReload()
    {
        await CreateTestZipAsync(Path.Combine(_rootDir, "test2.zip"), "file2.txt", "Hello from test2");

        var scope1 = VfsScope.Create(_serviceProvider, NullLogger<VfsScope>.Instance);
        await scope1.Vfs.MountAsync(new VfsMountOptions { RootPath = _rootDir, MaxDiscoveryDepth = 1 });

        var entries1 = await scope1.Vfs.ListDirectoryAsync("\\");
        entries1.Select(e => e.Name).Should().Contain("test2.zip");

        // Remove the ZIP
        File.Delete(Path.Combine(_rootDir, "test2.zip"));

        var scope2 = VfsScope.Create(_serviceProvider, NullLogger<VfsScope>.Instance);
        await scope2.Vfs.MountAsync(new VfsMountOptions { RootPath = _rootDir, MaxDiscoveryDepth = 1 });

        var entries2 = await scope2.Vfs.ListDirectoryAsync("\\");
        entries2.Select(e => e.Name).Should().NotContain("test2.zip");
        entries2.Select(e => e.Name).Should().Contain("test1.zip");

        await scope1.DisposeAsync();
        await scope2.DisposeAsync();
    }

    [Fact]
    public async Task OldScopeStillServesWhileNewIsBuilding()
    {
        var scope1 = VfsScope.Create(_serviceProvider, NullLogger<VfsScope>.Instance);
        await scope1.Vfs.MountAsync(new VfsMountOptions { RootPath = _rootDir, MaxDiscoveryDepth = 1 });

        // Read from scope1
        var exists = await scope1.Vfs.FileExistsAsync("\\test1.zip\\file1.txt");
        exists.Should().BeTrue();

        // Build scope2
        var scope2 = VfsScope.Create(_serviceProvider, NullLogger<VfsScope>.Instance);
        await scope2.Vfs.MountAsync(new VfsMountOptions { RootPath = _rootDir, MaxDiscoveryDepth = 1 });

        // scope1 still works
        var stillExists = await scope1.Vfs.FileExistsAsync("\\test1.zip\\file1.txt");
        stillExists.Should().BeTrue();

        // scope2 also works
        var scope2Exists = await scope2.Vfs.FileExistsAsync("\\test1.zip\\file1.txt");
        scope2Exists.Should().BeTrue();

        await scope1.DisposeAsync();
        await scope2.DisposeAsync();
    }

    [Fact]
    public async Task DisposeOldScope_DoesNotAffectNewScope()
    {
        var scope1 = VfsScope.Create(_serviceProvider, NullLogger<VfsScope>.Instance);
        await scope1.Vfs.MountAsync(new VfsMountOptions { RootPath = _rootDir, MaxDiscoveryDepth = 1 });

        var scope2 = VfsScope.Create(_serviceProvider, NullLogger<VfsScope>.Instance);
        await scope2.Vfs.MountAsync(new VfsMountOptions { RootPath = _rootDir, MaxDiscoveryDepth = 1 });

        // Dispose old scope
        await scope1.DisposeAsync();

        // New scope still works
        var entries = await scope2.Vfs.ListDirectoryAsync("\\");
        entries.Should().NotBeEmpty();

        var exists = await scope2.Vfs.FileExistsAsync("\\test1.zip\\file1.txt");
        exists.Should().BeTrue();

        await scope2.DisposeAsync();
    }

    [Fact]
    public async Task FailedMount_DoesNotDisruptExistingScope()
    {
        var scope1 = VfsScope.Create(_serviceProvider, NullLogger<VfsScope>.Instance);
        await scope1.Vfs.MountAsync(new VfsMountOptions { RootPath = _rootDir, MaxDiscoveryDepth = 1 });

        // Try mounting with nonexistent directory
        var scope2 = VfsScope.Create(_serviceProvider, NullLogger<VfsScope>.Instance);
        var act = async () => await scope2.Vfs.MountAsync(
            new VfsMountOptions { RootPath = Path.Combine(_rootDir, "nonexistent"), MaxDiscoveryDepth = 1 });

        // This may or may not throw depending on ArchiveDiscovery behavior.
        // Either way, scope1 should still work.

        try { await act(); } catch { }
        await scope2.DisposeAsync();

        // scope1 is unaffected
        var entries = await scope1.Vfs.ListDirectoryAsync("\\");
        entries.Should().NotBeEmpty();

        await scope1.DisposeAsync();
    }

    [Fact]
    public async Task SwapAdapter_OldVfsReturnedForCleanup()
    {
        var settings = Options.Create(new MountSettings { ShortCircuitShellMetadata = false });
        var adapter = new DokanFileSystemAdapter(settings, NullLogger<DokanFileSystemAdapter>.Instance);

        var scope1 = VfsScope.Create(_serviceProvider, NullLogger<VfsScope>.Instance);
        await scope1.Vfs.MountAsync(new VfsMountOptions { RootPath = _rootDir, MaxDiscoveryDepth = 1 });
        adapter.SetVfs(scope1.Vfs);

        var scope2 = VfsScope.Create(_serviceProvider, NullLogger<VfsScope>.Instance);
        await scope2.Vfs.MountAsync(new VfsMountOptions { RootPath = _rootDir, MaxDiscoveryDepth = 1 });

        var old = await adapter.SwapAsync(scope2.Vfs, TimeSpan.FromSeconds(5));
        old.Should().BeSameAs(scope1.Vfs);

        await scope1.DisposeAsync();
        await scope2.DisposeAsync();
    }

    // === Helpers ===

    private sealed class NullHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static async Task CreateTestZipAsync(string path, string entryName, string content)
    {
        using var fs = File.Create(path);
        using var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        await using var writer = new StreamWriter(entry.Open());
        await writer.WriteAsync(content);
    }
}
