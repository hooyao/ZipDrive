using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZipDrive.Application.Services;
using ZipDrive.Domain;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Exceptions;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.TestHelpers;

namespace ZipDrive.Domain.Tests;

public class ArchiveVirtualFileSystemTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempRoot;
    private ArchiveVirtualFileSystem _vfs = null!;

    public ArchiveVirtualFileSystemTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _tempRoot = Path.Combine(Path.GetTempPath(), "VfsTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public async Task InitializeAsync()
    {
        // Create test ZIP files
        CreateTestZip("games/doom.zip", new Dictionary<string, string>
        {
            ["readme.txt"] = "Hello from doom!",
            ["maps/e1m1.wad"] = new string('X', 1000),
            ["maps/e1m2.wad"] = new string('Y', 2000),
            ["maps/textures/brick.png"] = new string('B', 500),
        });

        CreateTestZip("docs/manual.zip", new Dictionary<string, string>
        {
            ["intro.txt"] = "Introduction",
            ["chapter1.txt"] = "Chapter 1 content",
        });

        CreateTestZip("backup.zip", new Dictionary<string, string>
        {
            ["data.bin"] = new string('D', 5000),
        });

        // Build VFS with real infrastructure
        var charComparer = CaseInsensitiveCharComparer.Instance; // Windows-style
        var archiveTrie = new ArchiveTrie(charComparer);
        var pathResolver = new PathResolver(archiveTrie);

        var readerFactory = new ZipReaderFactory();
        var metadataStore = new ZipFormatMetadataStore();
        var encodingDetector = new FilenameEncodingDetector(
            Microsoft.Extensions.Options.Options.Create(new MountSettings()),
            NullLogger<FilenameEncodingDetector>.Instance);
        var zipBuilder = new ZipStructureBuilder(readerFactory, encodingDetector, metadataStore,
            TimeProvider.System, NullLogger<ZipStructureBuilder>.Instance);
        var zipExtractor = new ZipEntryExtractor(readerFactory, metadataStore);
        var formatRegistry = new FormatRegistry([zipBuilder], [zipExtractor], Array.Empty<IPrefetchStrategy>());

        var discovery = new ArchiveDiscovery(formatRegistry, NullLogger<ArchiveDiscovery>.Instance);

        var structureStore = new ArchiveStructureStore(
            new LruEvictionPolicy(), TimeProvider.System, NullLoggerFactory.Instance);
        var cacheOpts = Microsoft.Extensions.Options.Options.Create(
            new CacheOptions { MemoryCacheSizeMb = 256, DiskCacheSizeMb = 256 });
        var structureCache = new ArchiveStructureCache(
            structureStore, formatRegistry,
            TimeProvider.System, cacheOpts, NullLogger<ArchiveStructureCache>.Instance);

        var fileContentCache = new FileContentCache(
            formatRegistry,
            cacheOpts,
            new LruEvictionPolicy(), TimeProvider.System,
            NullLoggerFactory.Instance);

        _vfs = new ArchiveVirtualFileSystem(
            archiveTrie, structureCache, fileContentCache,
            discovery, pathResolver,
            new NullHostApplicationLifetime(),
            formatRegistry,
            Options.Create(new MountSettings { UseFolderNameAsVolumeLabel = true }),
            Options.Create(new PrefetchOptions { Enabled = false }),
            NullLogger<ArchiveVirtualFileSystem>.Instance);

        await _vfs.MountAsync(new VfsMountOptions { RootPath = _tempRoot, MaxDiscoveryDepth = 6 });
    }

    public async Task DisposeAsync()
    {
        if (_vfs?.IsMounted == true)
            await _vfs.UnmountAsync();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup - temp directory may be locked
        }
    }

    private void CreateTestZip(string relativePath, Dictionary<string, string> files)
    {
        string fullPath = Path.Combine(_tempRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        using (ZipArchive archive = ZipFile.Open(fullPath, ZipArchiveMode.Create))
        {
            foreach ((string name, string content) in files)
            {
                // Create parent directories
                string[] parts = name.Split('/');
                if (parts.Length > 1)
                {
                    string dirPath = string.Join('/', parts[..^1]) + "/";
                    if (!files.ContainsKey(dirPath))
                    {
                        try
                        {
                            archive.CreateEntry(dirPath);
                        }
                        catch
                        {
                            /* ignore duplicate */
                        }
                    }
                }

                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using (StreamWriter writer = new(entry.Open()))
                {
                    writer.Write(content);
                }
            }
        }
    }

    // === 9.13 Mount/Unmount lifecycle ===

    [Fact]
    public void AfterMount_IsMountedIsTrue()
    {
        _vfs.IsMounted.Should().BeTrue();
    }

    [Fact]
    public async Task Unmount_SetsIsMountedFalse()
    {
        await _vfs.UnmountAsync();
        _vfs.IsMounted.Should().BeFalse();
    }

    [Fact]
    public async Task OperationsAfterUnmount_ThrowInvalidOperation()
    {
        await _vfs.UnmountAsync();

        Func<Task> act = () => _vfs.ListDirectoryAsync("");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MountStateChanged_RaisedOnMount()
    {
        // Create a fresh VFS to test event
        var trie = new ArchiveTrie(CaseInsensitiveCharComparer.Instance);
        var resolver = new PathResolver(trie);
        var factory = new ZipReaderFactory();
        var ms2 = new ZipFormatMetadataStore();
        var detector2 = new FilenameEncodingDetector(
            Microsoft.Extensions.Options.Options.Create(new MountSettings()),
            NullLogger<FilenameEncodingDetector>.Instance);
        var zipBuilder2 = new ZipStructureBuilder(factory, detector2, ms2,
            TimeProvider.System, NullLogger<ZipStructureBuilder>.Instance);
        var zipExtractor2 = new ZipEntryExtractor(factory, ms2);
        var formatRegistry2 = new FormatRegistry([zipBuilder2], [zipExtractor2], Array.Empty<IPrefetchStrategy>());
        var discovery = new ArchiveDiscovery(formatRegistry2, NullLogger<ArchiveDiscovery>.Instance);
        var structStore = new ArchiveStructureStore(
            new LruEvictionPolicy(), TimeProvider.System, NullLoggerFactory.Instance);
        var cacheOpts2 = Microsoft.Extensions.Options.Options.Create(
            new CacheOptions { MemoryCacheSizeMb = 64, DiskCacheSizeMb = 64 });
        var structCache = new ArchiveStructureCache(structStore, formatRegistry2,
            TimeProvider.System, cacheOpts2, NullLogger<ArchiveStructureCache>.Instance);
        var fc = new FileContentCache(
            formatRegistry2,
            cacheOpts2,
            new LruEvictionPolicy(), TimeProvider.System,
            NullLoggerFactory.Instance);
        var vfs = new ArchiveVirtualFileSystem(trie, structCache, fc, discovery, resolver,
            new NullHostApplicationLifetime(),
            formatRegistry2,
            Options.Create(new MountSettings()),
            Options.Create(new PrefetchOptions { Enabled = false }),
            NullLogger<ArchiveVirtualFileSystem>.Instance);

        bool? eventValue = null;
        vfs.MountStateChanged += (_, mounted) => eventValue = mounted;

        await vfs.MountAsync(new VfsMountOptions { RootPath = _tempRoot });

        eventValue.Should().BeTrue();
    }

    // === 9.14 ListDirectoryAsync ===

    [Fact]
    public async Task ListDirectory_Root_ContainsVirtualFoldersAndArchives()
    {
        var entries = await _vfs.ListDirectoryAsync("");

        entries.Should().Contain(e => e.Name == "games" && e.IsDirectory);
        entries.Should().Contain(e => e.Name == "docs" && e.IsDirectory);
        entries.Should().Contain(e => e.Name == "backup.zip" && e.IsDirectory);
    }

    [Fact]
    public async Task ListDirectory_VirtualFolder_ContainsArchive()
    {
        var entries = await _vfs.ListDirectoryAsync("games");

        entries.Should().Contain(e => e.Name == "doom.zip" && e.IsDirectory);
    }

    [Fact]
    public async Task ListDirectory_ArchiveRoot_ContainsZipEntries()
    {
        var entries = await _vfs.ListDirectoryAsync("games/doom.zip");

        entries.Should().Contain(e => e.Name == "readme.txt" && !e.IsDirectory);
        entries.Should().Contain(e => e.Name == "maps" && e.IsDirectory);
    }

    [Fact]
    public async Task ListDirectory_ArchiveSubdir_ContainsFiles()
    {
        var entries = await _vfs.ListDirectoryAsync("games/doom.zip/maps");

        entries.Should().Contain(e => e.Name == "e1m1.wad");
        entries.Should().Contain(e => e.Name == "e1m2.wad");
        entries.Should().Contain(e => e.Name == "textures" && e.IsDirectory);
    }

    [Fact]
    public async Task ListDirectory_NonExistent_ThrowsVfsDirectoryNotFound()
    {
        Func<Task> act = () => _vfs.ListDirectoryAsync("nonexistent");

        await act.Should().ThrowAsync<VfsDirectoryNotFoundException>();
    }

    // === 9.15 GetFileInfoAsync ===

    [Fact]
    public async Task GetFileInfo_VirtualRoot_IsDirectory()
    {
        var info = await _vfs.GetFileInfoAsync("");

        info.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task GetFileInfo_VirtualFolder_IsDirectory()
    {
        var info = await _vfs.GetFileInfoAsync("games");

        info.IsDirectory.Should().BeTrue();
        info.Name.Should().Be("games");
    }

    [Fact]
    public async Task GetFileInfo_ArchiveRoot_IsDirectoryWithSize()
    {
        var info = await _vfs.GetFileInfoAsync("games/doom.zip");

        info.IsDirectory.Should().BeTrue();
        info.Name.Should().Be("doom.zip");
        info.SizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetFileInfo_FileInsideArchive_HasCorrectMetadata()
    {
        var info = await _vfs.GetFileInfoAsync("games/doom.zip/readme.txt");

        info.IsDirectory.Should().BeFalse();
        info.Name.Should().Be("readme.txt");
        info.SizeBytes.Should().Be(16); // "Hello from doom!" = 16 chars
    }

    [Fact]
    public async Task GetFileInfo_NonExistent_ThrowsVfsFileNotFound()
    {
        Func<Task> act = () => _vfs.GetFileInfoAsync("games/doom.zip/nonexistent.txt");

        await act.Should().ThrowAsync<VfsFileNotFoundException>();
    }

    // === 9.16 ReadFileAsync ===

    [Fact]
    public async Task ReadFile_EntireFile_ReturnsCorrectContent()
    {
        byte[] buffer = new byte[4096];
        int bytesRead = await _vfs.ReadFileAsync("games/doom.zip/readme.txt", buffer, 0);

        bytesRead.Should().Be(16);
        string content = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
        content.Should().Be("Hello from doom!");
    }

    [Fact]
    public async Task ReadFile_WithOffset_ReturnsCorrectSlice()
    {
        byte[] buffer = new byte[4096];
        int bytesRead = await _vfs.ReadFileAsync("games/doom.zip/readme.txt", buffer, 6);

        bytesRead.Should().Be(10); // "from doom!" = 10 chars
        string content = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
        content.Should().Be("from doom!");
    }

    [Fact]
    public async Task ReadFile_AtEof_ReturnsZero()
    {
        byte[] buffer = new byte[4096];
        int bytesRead = await _vfs.ReadFileAsync("games/doom.zip/readme.txt", buffer, 16);

        bytesRead.Should().Be(0);
    }

    [Fact]
    public async Task ReadFile_BeyondEof_ReturnsZero()
    {
        byte[] buffer = new byte[4096];
        int bytesRead = await _vfs.ReadFileAsync("games/doom.zip/readme.txt", buffer, 1000);

        bytesRead.Should().Be(0);
    }

    [Fact]
    public async Task ReadFile_CacheHit_ReturnsSameContent()
    {
        byte[] buffer1 = new byte[4096];
        byte[] buffer2 = new byte[4096];

        int read1 = await _vfs.ReadFileAsync("games/doom.zip/readme.txt", buffer1, 0);
        int read2 = await _vfs.ReadFileAsync("games/doom.zip/readme.txt", buffer2, 0);

        read1.Should().Be(read2);
        buffer1.AsSpan(0, read1).SequenceEqual(buffer2.AsSpan(0, read2)).Should().BeTrue();
    }

    [Fact]
    public async Task ReadFile_NonExistent_ThrowsVfsFileNotFound()
    {
        byte[] buffer = new byte[4096];
        Func<Task> act = () => _vfs.ReadFileAsync("games/doom.zip/nonexistent.txt", buffer, 0);

        await act.Should().ThrowAsync<VfsFileNotFoundException>();
    }

    [Fact]
    public async Task ReadFile_Directory_ThrowsVfsAccessDenied()
    {
        byte[] buffer = new byte[4096];
        Func<Task> act = () => _vfs.ReadFileAsync("games/doom.zip/maps", buffer, 0);

        await act.Should().ThrowAsync<VfsAccessDeniedException>();
    }

    // === 9.17 FileExistsAsync / DirectoryExistsAsync ===

    [Fact]
    public async Task FileExists_ExistingFile_ReturnsTrue()
    {
        (await _vfs.FileExistsAsync("games/doom.zip/readme.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task FileExists_Directory_ReturnsFalse()
    {
        (await _vfs.FileExistsAsync("games/doom.zip/maps")).Should().BeFalse();
    }

    [Fact]
    public async Task FileExists_NonExistent_ReturnsFalse()
    {
        (await _vfs.FileExistsAsync("games/doom.zip/nope.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task DirectoryExists_VirtualFolder_ReturnsTrue()
    {
        (await _vfs.DirectoryExistsAsync("games")).Should().BeTrue();
    }

    [Fact]
    public async Task DirectoryExists_Archive_ReturnsTrue()
    {
        (await _vfs.DirectoryExistsAsync("games/doom.zip")).Should().BeTrue();
    }

    [Fact]
    public async Task DirectoryExists_ArchiveDir_ReturnsTrue()
    {
        (await _vfs.DirectoryExistsAsync("games/doom.zip/maps")).Should().BeTrue();
    }

    [Fact]
    public async Task DirectoryExists_NonExistent_ReturnsFalse()
    {
        (await _vfs.DirectoryExistsAsync("nonexistent")).Should().BeFalse();
    }

    // === 9.18 Exception wrapping ===

    [Fact]
    public async Task ReadFile_VirtualRoot_ThrowsVfsAccessDenied()
    {
        byte[] buffer = new byte[4096];
        Func<Task> act = () => _vfs.ReadFileAsync("", buffer, 0);

        await act.Should().ThrowAsync<VfsAccessDeniedException>();
    }

    [Fact]
    public async Task ReadFile_VirtualFolder_ThrowsVfsAccessDenied()
    {
        byte[] buffer = new byte[4096];
        Func<Task> act = () => _vfs.ReadFileAsync("games", buffer, 0);

        await act.Should().ThrowAsync<VfsAccessDeniedException>();
    }

    [Fact]
    public async Task ReadFile_ArchiveRoot_ThrowsVfsAccessDenied()
    {
        byte[] buffer = new byte[4096];
        Func<Task> act = () => _vfs.ReadFileAsync("games/doom.zip", buffer, 0);

        await act.Should().ThrowAsync<VfsAccessDeniedException>();
    }

    // === Volume info ===

    [Fact]
    public void GetVolumeInfo_ReturnsNtfsReadOnly()
    {
        var vol = _vfs.GetVolumeInfo();

        vol.FileSystemName.Should().Be("NTFS");
        vol.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void GetVolumeInfo_VolumeLabelIsFolderName()
    {
        var vol = _vfs.GetVolumeInfo();

        string expectedLabel = Path.GetFileName(_tempRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        vol.VolumeLabel.Should().Be(expectedLabel);
    }

    [Fact]
    public void GetVolumeInfo_TotalBytesIsSumOfArchiveSizes()
    {
        var vol = _vfs.GetVolumeInfo();

        vol.TotalBytes.Should().BeGreaterThan(0, "mounted archives have non-zero sizes");
        vol.FreeBytes.Should().Be(0);
    }

    [Fact]
    public async Task GetVolumeInfo_LongFolderName_TruncatedTo32Chars()
    {
        // Create a temp dir with a 40-char folder name
        string longName = new string('A', 40);
        string longRoot = Path.Combine(Path.GetTempPath(), longName);
        Directory.CreateDirectory(longRoot);
        try
        {
            // Create a minimal ZIP so discovery finds something
            string zipPath = Path.Combine(longRoot, "test.zip");
            using (ZipFile.Open(zipPath, ZipArchiveMode.Create)) { }

            var trie = new ArchiveTrie(CaseInsensitiveCharComparer.Instance);
            var resolver = new PathResolver(trie);
            var readerFactory = new ZipReaderFactory();
            var ms3 = new ZipFormatMetadataStore();
            var detector = new FilenameEncodingDetector(Options.Create(new MountSettings()), NullLogger<FilenameEncodingDetector>.Instance);
            var zipBuilder3 = new ZipStructureBuilder(readerFactory, detector, ms3,
                TimeProvider.System, NullLogger<ZipStructureBuilder>.Instance);
            var zipExtractor3 = new ZipEntryExtractor(readerFactory, ms3);
            var formatRegistry3 = new FormatRegistry([zipBuilder3], [zipExtractor3], Array.Empty<IPrefetchStrategy>());
            var discovery = new ArchiveDiscovery(formatRegistry3, NullLogger<ArchiveDiscovery>.Instance);
            var cacheOpts = Options.Create(new CacheOptions { MemoryCacheSizeMb = 64, DiskCacheSizeMb = 64 });
            var structureStore = new ArchiveStructureStore(new LruEvictionPolicy(), TimeProvider.System, NullLoggerFactory.Instance);
            var structCache = new ArchiveStructureCache(structureStore, formatRegistry3, TimeProvider.System, cacheOpts, NullLogger<ArchiveStructureCache>.Instance);
            var fc = new FileContentCache(formatRegistry3, cacheOpts, new LruEvictionPolicy(), TimeProvider.System, NullLoggerFactory.Instance);

            var vfs = new ArchiveVirtualFileSystem(trie, structCache, fc, discovery, resolver,
                new NullHostApplicationLifetime(),
                formatRegistry3,
                Options.Create(new MountSettings { UseFolderNameAsVolumeLabel = true }),
                Options.Create(new PrefetchOptions { Enabled = false }),
                NullLogger<ArchiveVirtualFileSystem>.Instance);

            await vfs.MountAsync(new VfsMountOptions { RootPath = longRoot, MaxDiscoveryDepth = 1 });

            var vol = vfs.GetVolumeInfo();
            vol.VolumeLabel.Should().HaveLength(32);
            vol.VolumeLabel.Should().Be(longName[..32]);

            await vfs.UnmountAsync();
        }
        finally
        {
            try { Directory.Delete(longRoot, true); } catch { }
        }
    }
}
