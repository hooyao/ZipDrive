using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip;
using ZipDrive.Infrastructure.Caching;

namespace ZipDrive.Benchmarks;

/// <summary>
/// Benchmarks encoding detection overhead in ArchiveStructureCache.BuildStructureAsync.
/// Compares: ASCII (no detection), UTF-8 flagged (immediate insert), Shift-JIS (archive detection),
/// and mixed-encoding (per-entry detection).
/// </summary>
[MemoryDiagnoser]
public class EncodingDetectionBenchmarks
{
    private string _asciiZipPath = "";
    private string _utf8ZipPath = "";
    private string _shiftJisZipPath = "";
    private string _mixedZipPath = "";
    private string _tempDir = "";

    private IFilenameEncodingDetector _detector = null!;

    [Params(100, 500, 1000)]
    public int EntryCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        _tempDir = Path.Combine(Path.GetTempPath(), $"ZipBench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _detector = new FilenameEncodingDetector(
            Options.Create(new MountSettings()),
            NullLogger<FilenameEncodingDetector>.Instance);

        // Generate deterministic filenames
        var asciiEntries = GenerateAsciiEntries(EntryCount);
        var japaneseEntries = GenerateJapaneseEntries(EntryCount);
        var mixedEntries = GenerateMixedEntries(EntryCount);

        _asciiZipPath = BuildZip("ascii.zip", asciiEntries, utf8Flag: false);
        _utf8ZipPath = BuildZip("utf8.zip", asciiEntries, utf8Flag: true);
        _shiftJisZipPath = BuildZipWithCodePage("sjis.zip", japaneseEntries, 932);
        _mixedZipPath = BuildMixedEncodingZip("mixed.zip", mixedEntries);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Benchmark(Baseline = true, Description = "ASCII (no detection)")]
    public async Task<ArchiveStructure> Ascii_NoDetection()
    {
        return await BuildStructure(_asciiZipPath);
    }

    [Benchmark(Description = "UTF-8 flagged (immediate insert)")]
    public async Task<ArchiveStructure> Utf8_ImmediateInsert()
    {
        return await BuildStructure(_utf8ZipPath);
    }

    [Benchmark(Description = "Shift-JIS (archive detection)")]
    public async Task<ArchiveStructure> ShiftJis_ArchiveDetection()
    {
        return await BuildStructure(_shiftJisZipPath);
    }

    [Benchmark(Description = "Mixed JP+CN (per-entry detection)")]
    public async Task<ArchiveStructure> Mixed_PerEntryDetection()
    {
        return await BuildStructure(_mixedZipPath);
    }

    private async Task<ArchiveStructure> BuildStructure(string zipPath)
    {
        var readerFactory = new ZipReaderFactory();
        var structureStore = new ArchiveStructureStore(
            new LruEvictionPolicy(), TimeProvider.System, NullLoggerFactory.Instance);
        var cacheOpts = Options.Create(new CacheOptions { MemoryCacheSizeMb = 256, DiskCacheSizeMb = 256 });

        var cache = new ArchiveStructureCache(
            structureStore, readerFactory, TimeProvider.System, cacheOpts,
            NullLogger<ArchiveStructureCache>.Instance, _detector);

        return await cache.GetOrBuildAsync($"bench_{Guid.NewGuid():N}", zipPath);
    }

    #region Test Data Generation

    private static List<string> GenerateAsciiEntries(int count)
    {
        string[] dirs = ["docs", "src", "tests", "config", "data", "lib", "build"];
        var entries = new List<string>(count);
        var rng = new Random(42);
        for (int i = 0; i < count; i++)
        {
            int depth = rng.Next(1, 4);
            string path = string.Join("/", Enumerable.Range(0, depth).Select(d => dirs[(i + d) % dirs.Length]));
            entries.Add($"{path}/file{i:D6}.dat");
        }
        return entries;
    }

    private static List<string> GenerateJapaneseEntries(int count)
    {
        string[] dirs = ["テスト", "文書", "データ", "設定", "画像", "報告", "資料"];
        var entries = new List<string>(count);
        var rng = new Random(42);
        for (int i = 0; i < count; i++)
        {
            int depth = rng.Next(1, 4);
            string path = string.Join("/", Enumerable.Range(0, depth).Select(d => dirs[(i + d) % dirs.Length]));
            entries.Add($"{path}/ファイル{i:D6}.dat");
        }
        return entries;
    }

    private static List<(string Name, int CodePage)> GenerateMixedEntries(int count)
    {
        string[] jaDirs = ["テスト", "文書", "データ"];
        string[] cnDirs = ["测试", "文档", "数据"];
        var entries = new List<(string, int)>(count);
        var rng = new Random(42);
        for (int i = 0; i < count; i++)
        {
            bool isJapanese = i % 2 == 0;
            var dirs = isJapanese ? jaDirs : cnDirs;
            int codePage = isJapanese ? 932 : 936;
            int depth = rng.Next(1, 4);
            string path = string.Join("/", Enumerable.Range(0, depth).Select(d => dirs[(i + d) % dirs.Length]));
            string suffix = isJapanese ? $"ファイル{i:D6}.dat" : $"文件{i:D6}.dat";
            entries.Add(($"{path}/{suffix}", codePage));
        }
        return entries;
    }

    #endregion

    #region Binary ZIP Builder

    private string BuildZip(string name, List<string> fileNames, bool utf8Flag)
    {
        string path = Path.Combine(_tempDir, name);
        using var ms = new MemoryStream();
        var offsets = new List<long>();
        var nameBytes = new List<byte[]>();
        ushort flag = utf8Flag ? (ushort)0x0800 : (ushort)0;
        Encoding enc = utf8Flag ? Encoding.UTF8 : Encoding.GetEncoding(437);

        foreach (string fileName in fileNames)
        {
            byte[] nb = enc.GetBytes(fileName);
            nameBytes.Add(nb);
            offsets.Add(ms.Position);
            WriteLocalHeader(ms, nb, flag);
        }

        long cdOffset = ms.Position;
        for (int i = 0; i < fileNames.Count; i++)
            WriteCdEntry(ms, nameBytes[i], offsets[i], flag);

        WriteEocd(ms, fileNames.Count, ms.Position - cdOffset, cdOffset);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private string BuildZipWithCodePage(string name, List<string> fileNames, int codePage)
    {
        string path = Path.Combine(_tempDir, name);
        Encoding enc = Encoding.GetEncoding(codePage);
        using var ms = new MemoryStream();
        var offsets = new List<long>();
        var nameBytes = new List<byte[]>();

        foreach (string fileName in fileNames)
        {
            byte[] nb = enc.GetBytes(fileName);
            nameBytes.Add(nb);
            offsets.Add(ms.Position);
            WriteLocalHeader(ms, nb, 0);
        }

        long cdOffset = ms.Position;
        for (int i = 0; i < fileNames.Count; i++)
            WriteCdEntry(ms, nameBytes[i], offsets[i], 0);

        WriteEocd(ms, fileNames.Count, ms.Position - cdOffset, cdOffset);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private string BuildMixedEncodingZip(string name, List<(string Name, int CodePage)> entries)
    {
        string path = Path.Combine(_tempDir, name);
        using var ms = new MemoryStream();
        var offsets = new List<long>();
        var nameBytes = new List<byte[]>();

        foreach (var (fileName, codePage) in entries)
        {
            byte[] nb = Encoding.GetEncoding(codePage).GetBytes(fileName);
            nameBytes.Add(nb);
            offsets.Add(ms.Position);
            WriteLocalHeader(ms, nb, 0);
        }

        long cdOffset = ms.Position;
        for (int i = 0; i < entries.Count; i++)
            WriteCdEntry(ms, nameBytes[i], offsets[i], 0);

        WriteEocd(ms, entries.Count, ms.Position - cdOffset, cdOffset);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static void WriteLocalHeader(Stream s, byte[] nameBytes, ushort flag)
    {
        WriteU32(s, 0x04034b50);
        WriteU16(s, 20);
        WriteU16(s, flag);
        WriteU16(s, 0);
        WriteU16(s, 0);
        WriteU16(s, 0x5521);
        WriteU32(s, 0);
        WriteU32(s, 0);
        WriteU32(s, 0);
        WriteU16(s, (ushort)nameBytes.Length);
        WriteU16(s, 0);
        s.Write(nameBytes);
    }

    private static void WriteCdEntry(Stream s, byte[] nameBytes, long localOffset, ushort flag)
    {
        WriteU32(s, 0x02014b50);
        WriteU16(s, 20);
        WriteU16(s, 20);
        WriteU16(s, flag);
        WriteU16(s, 0);
        WriteU16(s, 0);
        WriteU16(s, 0x5521);
        WriteU32(s, 0);
        WriteU32(s, 0);
        WriteU32(s, 0);
        WriteU16(s, (ushort)nameBytes.Length);
        WriteU16(s, 0);
        WriteU16(s, 0);
        WriteU16(s, 0);
        WriteU16(s, 0);
        WriteU32(s, 0x20);
        WriteU32(s, (uint)localOffset);
        s.Write(nameBytes);
    }

    private static void WriteEocd(Stream s, int count, long cdSize, long cdOffset)
    {
        WriteU32(s, 0x06054b50);
        WriteU16(s, 0);
        WriteU16(s, 0);
        WriteU16(s, (ushort)count);
        WriteU16(s, (ushort)count);
        WriteU32(s, (uint)cdSize);
        WriteU32(s, (uint)cdOffset);
        WriteU16(s, 0);
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        s.Write(b);
    }

    #endregion
}
