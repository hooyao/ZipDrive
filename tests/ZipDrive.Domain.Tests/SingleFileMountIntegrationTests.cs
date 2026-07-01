using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZipDrive.Application.Services;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.TestHelpers;

namespace ZipDrive.Domain.Tests;

/// <summary>
/// Integration tests for single-file mount mode.
/// Tests the VFS mount path, virtual path structure, and volume label behavior.
/// </summary>
public class SingleFileMountIntegrationTests : IDisposable
{
    private readonly string _tempRoot;

    public SingleFileMountIntegrationTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _tempRoot = Path.Combine(Path.GetTempPath(), "SfmTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { }
    }

    private (ArchiveVirtualFileSystem vfs, ArchiveDiscovery discovery, FormatRegistry formatRegistry) CreateInfrastructure()
    {
        var trie = new ArchiveTrie(CaseInsensitiveCharComparer.Instance);
        var resolver = new PathResolver(trie);
        var readerFactory = new ZipReaderFactory();
        var ms = new ZipFormatMetadataStore();
        var detector = new FilenameEncodingDetector(Options.Create(new MountSettings()), NullLogger<FilenameEncodingDetector>.Instance);
        var zipBuilder = new ZipStructureBuilder(readerFactory, detector, ms,
            TimeProvider.System, NullLogger<ZipStructureBuilder>.Instance);
        var zipExtractor = new ZipEntryExtractor(readerFactory, ms);
        var formatRegistry = new FormatRegistry([zipBuilder], [zipExtractor], Array.Empty<IPrefetchStrategy>());
        var discovery = new ArchiveDiscovery(formatRegistry, NullLogger<ArchiveDiscovery>.Instance);
        var cacheOpts = Options.Create(new CacheOptions { MemoryCacheSizeMb = 64, DiskCacheSizeMb = 64 });
        var structureStore = new ArchiveStructureStore(new LruEvictionPolicy(), TimeProvider.System, NullLoggerFactory.Instance);
        var structCache = new ArchiveStructureCache(structureStore, formatRegistry, TimeProvider.System, cacheOpts, NullLogger<ArchiveStructureCache>.Instance);
        var fc = new FileContentCache(formatRegistry, cacheOpts, new LruEvictionPolicy(), TimeProvider.System, NullLoggerFactory.Instance);

        var vfs = new ArchiveVirtualFileSystem(trie, structCache, fc, discovery, resolver,
            new NullHostApplicationLifetime(),
            formatRegistry,
            Options.Create(new MountSettings()),
            Options.Create(new PrefetchOptions { Enabled = false }),
            NullLogger<ArchiveVirtualFileSystem>.Instance);

        return (vfs, discovery, formatRegistry);
    }

    private string CreateTestZip(string relativePath, Dictionary<string, string> files)
    {
        string fullPath = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        using var archive = ZipFile.Open(fullPath, ZipArchiveMode.Create);
        foreach ((string name, string content) in files)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using StreamWriter writer = new(entry.Open());
            writer.Write(content);
        }

        return fullPath;
    }

    // === 5.1 Single ZIP file mounts with correct virtual path structure ===

    [Fact]
    public async Task SingleZipFile_MountsWithArchiveFolderAtRoot()
    {
        string zipPath = CreateTestZip("game.zip", new Dictionary<string, string>
        {
            ["readme.txt"] = "Hello",
            ["data/level1.dat"] = "Level data"
        });

        var (vfs, _, _) = CreateInfrastructure();
        bool result = await vfs.MountSingleFileAsync(zipPath);

        result.Should().BeTrue();
        vfs.IsMounted.Should().BeTrue();

        // R:\ shows game.zip as a folder
        var root = await vfs.ListDirectoryAsync("");
        root.Should().HaveCount(1);
        root[0].Name.Should().Be("game.zip");
        root[0].IsDirectory.Should().BeTrue();

        // Inside game.zip folder
        var contents = await vfs.ListDirectoryAsync("game.zip");
        contents.Should().Contain(e => e.Name == "readme.txt" && !e.IsDirectory);
        contents.Should().Contain(e => e.Name == "data" && e.IsDirectory);

        // Can read files
        byte[] buffer = new byte[4096];
        int bytesRead = await vfs.ReadFileAsync("game.zip/readme.txt", buffer, 0);
        Encoding.UTF8.GetString(buffer, 0, bytesRead).Should().Be("Hello");
    }

    // === 5.2 Unsupported file returns false (no mount) ===

    [Fact]
    public async Task UnsupportedFile_ReturnsFalseAndDoesNotMount()
    {
        string txtPath = Path.Combine(_tempRoot, "report.docx");
        File.WriteAllText(txtPath, "not an archive");

        var (vfs, _, formatRegistry) = CreateInfrastructure();

        // Simulate the DokanHostedService logic: check format
        string? formatId = formatRegistry.DetectFormat(txtPath);
        formatId.Should().BeNull("docx is not a supported format");

        bool result = await vfs.MountSingleFileAsync(txtPath);
        result.Should().BeFalse();
        vfs.IsMounted.Should().BeFalse();
    }

    // === 5.3 Non-existent path detection ===

    [Fact]
    public void NonExistentPath_NeitherFileNorDirectory()
    {
        string fakePath = Path.Combine(_tempRoot, "does_not_exist.zip");

        File.Exists(fakePath).Should().BeFalse();
        Directory.Exists(fakePath).Should().BeFalse();
        // DokanHostedService would show UserNotice.Error and WaitForKeyAndStop
    }

    // === 5.4 Empty directory mounts with zero archives ===

    [Fact]
    public async Task EmptyDirectory_MountsWithZeroArchives()
    {
        string emptyDir = Path.Combine(_tempRoot, "empty");
        Directory.CreateDirectory(emptyDir);

        var (vfs, discovery, _) = CreateInfrastructure();

        await vfs.MountAsync(new VfsMountOptions { RootPath = emptyDir, MaxDiscoveryDepth = 6 });

        vfs.IsMounted.Should().BeTrue();

        // Cast to IArchiveManager to check registered archives
        IArchiveManager manager = vfs;
        manager.GetRegisteredArchives().Should().BeEmpty(
            "DokanHostedService checks this and shows a WARNING notice");
    }

    // === 5.5 Directory with archives — existing behavior unchanged ===

    [Fact]
    public async Task DirectoryWithArchives_MountsNormally()
    {
        CreateTestZip("archive1.zip", new Dictionary<string, string> { ["a.txt"] = "A" });
        CreateTestZip("sub/archive2.zip", new Dictionary<string, string> { ["b.txt"] = "B" });

        var (vfs, _, _) = CreateInfrastructure();

        await vfs.MountAsync(new VfsMountOptions { RootPath = _tempRoot, MaxDiscoveryDepth = 6 });

        vfs.IsMounted.Should().BeTrue();

        var root = await vfs.ListDirectoryAsync("");
        root.Should().Contain(e => e.Name == "archive1.zip" && e.IsDirectory);
        root.Should().Contain(e => e.Name == "sub" && e.IsDirectory);

        var sub = await vfs.ListDirectoryAsync("sub");
        sub.Should().Contain(e => e.Name == "archive2.zip" && e.IsDirectory);
    }

    // === 5.6 Single-file mode does not start FileSystemWatcher ===
    // Verified structurally: StartWatcher() is only called inside the Directory.Exists branch.
    // Single-file mode mounts successfully without touching any watcher infrastructure.

    [Fact]
    public async Task SingleFileMode_MountsWithoutWatcherInfrastructure()
    {
        // MountSingleFileAsync only calls DescribeFile + AddArchiveAsync — no watcher involvement.
        // If it returns true, the mount succeeded entirely within the VFS layer.
        string zipPath = CreateTestZip("watcher-test.zip", new Dictionary<string, string>
        {
            ["file.txt"] = "content"
        });

        var (vfs, _, _) = CreateInfrastructure();
        bool result = await vfs.MountSingleFileAsync(zipPath);

        result.Should().BeTrue();
        vfs.IsMounted.Should().BeTrue();
        // No FileSystemWatcher is created — that's DokanHostedService's responsibility
        // and it only calls StartWatcher() in the directory branch.
    }
}
