using BenchmarkDotNet.Attributes;
using KTrie;
using ZipDrive.Domain.Models;

namespace ZipDrive.Benchmarks;

/// <summary>
/// Benchmarks for ArchiveStructure trie operations.
/// </summary>
[MemoryDiagnoser]
public class ArchiveStructureBenchmarks
{
    private ArchiveStructure _structure = null!;
    private string _deepPath = "";

    [Params(1000, 10000, 100000)]
    public int EntryCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var trie = new TrieDictionary<ZipEntryInfo>();
        var rng = new Random(42);
        string[] dirs = ["docs", "maps", "textures", "sounds", "scripts", "data", "config"];

        for (int i = 0; i < EntryCount; i++)
        {
            int depth = rng.Next(1, 6);
            string path = string.Join("/", Enumerable.Range(0, depth).Select(d => dirs[d % dirs.Length]));
            string fullPath = $"{path}/file{i:D6}.dat";

            trie[fullPath] = new ZipEntryInfo
            {
                LocalHeaderOffset = i * 1024L,
                CompressedSize = rng.Next(100, 10000),
                UncompressedSize = rng.Next(100, 50000),
                CompressionMethod = 8,
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Attributes = FileAttributes.Normal,
                Crc32 = (uint)rng.Next()
            };
        }

        // Add directory entries
        foreach (string dir in dirs)
        {
            trie[dir + "/"] = new ZipEntryInfo
            {
                LocalHeaderOffset = 0,
                CompressedSize = 0,
                UncompressedSize = 0,
                CompressionMethod = 0,
                IsDirectory = true,
                Attributes = FileAttributes.Directory,
                LastModified = DateTime.UtcNow
            };
        }

        _structure = new ArchiveStructure
        {
            ArchiveKey = "benchmark.zip",
            AbsolutePath = @"D:\test\benchmark.zip",
            Entries = trie,
            BuiltAt = DateTimeOffset.UtcNow,
            TotalUncompressedSize = 0,
            EstimatedMemoryBytes = trie.Count * 114
        };

        _deepPath = $"docs/maps/textures/sounds/scripts/file000042.dat";
    }

    [Benchmark]
    public ZipEntryInfo? GetEntry_ShallowPath()
        => _structure.GetEntry("docs/file000001.dat");

    [Benchmark]
    public ZipEntryInfo? GetEntry_DeepPath()
        => _structure.GetEntry(_deepPath);

    [Benchmark]
    public bool DirectoryExists()
        => _structure.DirectoryExists("docs");

    [Benchmark]
    public int ListDirectory_Root()
        => _structure.ListDirectory("").Count();

    [Benchmark]
    public int ListDirectory_Subdirectory()
        => _structure.ListDirectory("docs").Count();
}
