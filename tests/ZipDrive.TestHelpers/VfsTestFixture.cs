using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using ZipDrive.Application.Services;
using ZipDrive.Domain;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;
using ZipDrive.Domain.Configuration;
using ZipDrive.Infrastructure.Archives.Zip;
using ZipDrive.Infrastructure.Caching;

namespace ZipDrive.TestHelpers;

/// <summary>
/// Shared test fixture that generates test ZIPs once for an entire test collection.
/// Implements IAsyncLifetime to generate on setup and clean up on teardown.
/// </summary>
public class VfsTestFixture : IAsyncLifetime
{
    public string RootPath { get; private set; } = "";
    public IVirtualFileSystem Vfs { get; private set; } = null!;
    public IArchiveTrie ArchiveTrie { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        RootPath = Path.Combine(Path.GetTempPath(), "VfsFixture_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(RootPath);

        // Generate small-scale fixture for fast CI
        await TestZipGenerator.GenerateTestFixtureAsync(RootPath, smallScale: true);

        // Build VFS
        var charComparer = CaseInsensitiveCharComparer.Instance;
        var archiveTrie = new ArchiveTrie(charComparer);
        ArchiveTrie = archiveTrie;
        var pathResolver = new PathResolver(archiveTrie);
        var discovery = new ArchiveDiscovery(NullLogger<ArchiveDiscovery>.Instance);

        var readerFactory = new ZipReaderFactory();

        var structureStore = new ArchiveStructureStore(
            new LruEvictionPolicy(), TimeProvider.System, NullLoggerFactory.Instance);
        var cacheOpts = Microsoft.Extensions.Options.Options.Create(
            new CacheOptions { MemoryCacheSizeMb = 256, DiskCacheSizeMb = 256 });
        var encodingDetector = new FilenameEncodingDetector(
            Microsoft.Extensions.Options.Options.Create(new MountSettings()),
            NullLogger<FilenameEncodingDetector>.Instance);
        var structureCache = new ArchiveStructureCache(structureStore, readerFactory,
            TimeProvider.System, cacheOpts, NullLogger<ArchiveStructureCache>.Instance, encodingDetector);

        var fileContentCache = new FileContentCache(
            readerFactory, cacheOpts,
            new LruEvictionPolicy(), TimeProvider.System,
            NullLoggerFactory.Instance);

        var vfs = new ZipVirtualFileSystem(
            archiveTrie, structureCache, fileContentCache,
            discovery, pathResolver,
            new NullHostApplicationLifetime(),
            Options.Create(new PrefetchOptions { Enabled = false }),
            NullLogger<ZipVirtualFileSystem>.Instance);

        await vfs.MountAsync(new VfsMountOptions { RootPath = RootPath, MaxDiscoveryDepth = 6 });
        Vfs = vfs;
    }

    public async Task DisposeAsync()
    {
        if (Vfs is ZipVirtualFileSystem zvfs && zvfs.IsMounted)
            await zvfs.UnmountAsync();

        try
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException) { /* best effort */ }
    }
}

/// <summary>
/// xunit collection definition for sharing VfsTestFixture across test classes.
/// </summary>
[CollectionDefinition("VfsIntegration")]
public class VfsIntegrationCollection : ICollectionFixture<VfsTestFixture>
{
    // This class is never instantiated. It's just a marker for xunit.
}

/// <summary>Minimal stub for IHostApplicationLifetime used in tests.</summary>
public sealed class NullHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}
