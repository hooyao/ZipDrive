using BenchmarkDotNet.Attributes;
using ZipDriveV3.Application.Services;
using ZipDriveV3.Domain;
using ZipDriveV3.Domain.Models;

namespace ZipDriveV3.Benchmarks;

/// <summary>
/// Benchmarks for ArchiveTrie path resolution and folder listing.
/// </summary>
[MemoryDiagnoser]
public class ArchiveTrieBenchmarks
{
    private ArchiveTrie _trie = null!;

    [Params(10, 100, 1000)]
    public int ArchiveCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _trie = new ArchiveTrie(CaseInsensitiveCharComparer.Instance);
        var rng = new Random(42);
        string[] folders = ["games/fps", "games/rpg", "docs/manuals", "media/videos", "backup", "archives"];

        for (int i = 0; i < ArchiveCount; i++)
        {
            string folder = folders[i % folders.Length];
            _trie.AddArchive(new ArchiveDescriptor
            {
                VirtualPath = $"{folder}/archive{i:D4}.zip",
                PhysicalPath = $@"D:\test\{folder}\archive{i:D4}.zip",
                SizeBytes = rng.Next(1024, 100 * 1024 * 1024),
                LastModifiedUtc = DateTime.UtcNow
            });
        }
    }

    [Benchmark]
    public ArchiveTrieResult Resolve_InsideArchive()
        => _trie.Resolve("games/fps/archive0001.zip/maps/e1m1.wad");

    [Benchmark]
    public ArchiveTrieResult Resolve_VirtualFolder()
        => _trie.Resolve("games/fps");

    [Benchmark]
    public ArchiveTrieResult Resolve_ArchiveRoot()
        => _trie.Resolve("games/fps/archive0001.zip");

    [Benchmark]
    public ArchiveTrieResult Resolve_VirtualRoot()
        => _trie.Resolve("");

    [Benchmark]
    public ArchiveTrieResult Resolve_NotFound()
        => _trie.Resolve("nonexistent/path/file.txt");

    [Benchmark]
    public int ListFolder_Root()
        => _trie.ListFolder("").Count();

    [Benchmark]
    public int ListFolder_Subfolder()
        => _trie.ListFolder("games/fps").Count();
}
