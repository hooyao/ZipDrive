using System.Text;
using Xunit;
using ZipDriveV3.Application.Services;
using ZipDriveV3.Domain;
using ZipDriveV3.Domain.Abstractions;
using ZipDriveV3.Domain.Models;
using ZipDriveV3.Infrastructure.Archives.Zip;
using ZipDriveV3.Infrastructure.Caching;

namespace ZipDriveV3.TestHelpers;

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
        var discovery = new ArchiveDiscovery();

        Func<string, IZipReader> readerFactory = path => new ZipReader(File.OpenRead(path));

        var structureCacheStorage = new ObjectStorageStrategy<ArchiveStructure>();
        var structureCacheEviction = new LruEvictionPolicy();
        var structureCacheGeneric = new GenericCache<ArchiveStructure>(
            structureCacheStorage, structureCacheEviction, 256 * 1024 * 1024);
        var structureCache = new ArchiveStructureCache(structureCacheGeneric, readerFactory);

        var fileCacheStorage = new MemoryStorageStrategy();
        var fileCacheEviction = new LruEvictionPolicy();
        var fileCache = new GenericCache<Stream>(
            fileCacheStorage, fileCacheEviction, 256 * 1024 * 1024);

        var vfs = new ZipVirtualFileSystem(
            archiveTrie, structureCache, fileCache,
            discovery, pathResolver, readerFactory);

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
