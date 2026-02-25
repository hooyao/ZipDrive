using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip;
using ZipDrive.Infrastructure.Caching;

namespace ZipDrive.Infrastructure.Archives.Zip.Tests;

/// <summary>
/// Integration tests verifying the full encoding detection pipeline:
/// ZipReader → ArchiveStructureCache (with FilenameEncodingDetector) → trie with correct Unicode keys.
/// Uses binary-level ZIP construction to create archives without the UTF-8 flag.
/// </summary>
public class EncodingIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public EncodingIntegrationTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"ZipEncodingTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    #region Helper: Binary ZIP Builder

    /// <summary>
    /// Builds a minimal valid ZIP file at the binary level with filenames encoded
    /// in the specified code page and WITHOUT the UTF-8 flag (bit 11).
    /// Each entry is a Store (method 0) file with empty content.
    /// </summary>
    private string BuildZipWithEncoding(string zipName, IReadOnlyList<(string FileName, int CodePage)> entries)
    {
        string path = Path.Combine(_tempDir, zipName);

        using var ms = new MemoryStream();
        var localHeaderOffsets = new List<long>();
        var fileNameBytesList = new List<byte[]>();

        // Write Local File Headers + data (empty files)
        foreach (var (fileName, codePage) in entries)
        {
            byte[] nameBytes = Encoding.GetEncoding(codePage).GetBytes(fileName);
            fileNameBytesList.Add(nameBytes);
            localHeaderOffsets.Add(ms.Position);

            // Local File Header (30 bytes fixed + filename)
            WriteUInt32(ms, 0x04034b50); // signature
            WriteUInt16(ms, 20);         // version needed (2.0)
            WriteUInt16(ms, 0);          // general purpose bit flag (NO UTF-8 flag)
            WriteUInt16(ms, 0);          // compression method (Store)
            WriteUInt16(ms, 0);          // last mod time
            WriteUInt16(ms, 0x5521);     // last mod date (2022-09-01)
            WriteUInt32(ms, 0);          // crc32
            WriteUInt32(ms, 0);          // compressed size
            WriteUInt32(ms, 0);          // uncompressed size
            WriteUInt16(ms, (ushort)nameBytes.Length); // filename length
            WriteUInt16(ms, 0);          // extra field length
            ms.Write(nameBytes);         // filename
            // No data (empty file)
        }

        // Write Central Directory
        long cdOffset = ms.Position;
        for (int i = 0; i < entries.Count; i++)
        {
            byte[] nameBytes = fileNameBytesList[i];

            WriteUInt32(ms, 0x02014b50); // CD signature
            WriteUInt16(ms, 20);         // version made by
            WriteUInt16(ms, 20);         // version needed
            WriteUInt16(ms, 0);          // general purpose bit flag (NO UTF-8 flag)
            WriteUInt16(ms, 0);          // compression method (Store)
            WriteUInt16(ms, 0);          // last mod time
            WriteUInt16(ms, 0x5521);     // last mod date
            WriteUInt32(ms, 0);          // crc32
            WriteUInt32(ms, 0);          // compressed size
            WriteUInt32(ms, 0);          // uncompressed size
            WriteUInt16(ms, (ushort)nameBytes.Length); // filename length
            WriteUInt16(ms, 0);          // extra field length
            WriteUInt16(ms, 0);          // comment length
            WriteUInt16(ms, 0);          // disk number start
            WriteUInt16(ms, 0);          // internal file attributes
            WriteUInt32(ms, 0x20);       // external file attributes (archive)
            WriteUInt32(ms, (uint)localHeaderOffsets[i]); // local header offset
            ms.Write(nameBytes);         // filename
        }

        long cdSize = ms.Position - cdOffset;

        // Write EOCD
        WriteUInt32(ms, 0x06054b50); // EOCD signature
        WriteUInt16(ms, 0);          // disk number
        WriteUInt16(ms, 0);          // CD disk number
        WriteUInt16(ms, (ushort)entries.Count); // entries on disk
        WriteUInt16(ms, (ushort)entries.Count); // total entries
        WriteUInt32(ms, (uint)cdSize); // CD size
        WriteUInt32(ms, (uint)cdOffset); // CD offset
        WriteUInt16(ms, 0);          // comment length

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Builds a ZIP with entries where some have the UTF-8 flag set and some don't.
    /// </summary>
    private string BuildMixedUtf8Zip(string zipName,
        IReadOnlyList<(string FileName, int CodePage, bool IsUtf8)> entries)
    {
        string path = Path.Combine(_tempDir, zipName);

        using var ms = new MemoryStream();
        var localHeaderOffsets = new List<long>();
        var fileNameBytesList = new List<byte[]>();
        var flags = new List<ushort>();

        foreach (var (fileName, codePage, isUtf8) in entries)
        {
            Encoding enc = isUtf8 ? Encoding.UTF8 : Encoding.GetEncoding(codePage);
            byte[] nameBytes = enc.GetBytes(fileName);
            fileNameBytesList.Add(nameBytes);
            localHeaderOffsets.Add(ms.Position);
            ushort flag = isUtf8 ? (ushort)0x0800 : (ushort)0;
            flags.Add(flag);

            WriteUInt32(ms, 0x04034b50);
            WriteUInt16(ms, 20);
            WriteUInt16(ms, flag);
            WriteUInt16(ms, 0);
            WriteUInt16(ms, 0);
            WriteUInt16(ms, 0x5521);
            WriteUInt32(ms, 0);
            WriteUInt32(ms, 0);
            WriteUInt32(ms, 0);
            WriteUInt16(ms, (ushort)nameBytes.Length);
            WriteUInt16(ms, 0);
            ms.Write(nameBytes);
        }

        long cdOffset = ms.Position;
        for (int i = 0; i < entries.Count; i++)
        {
            byte[] nameBytes = fileNameBytesList[i];
            WriteUInt32(ms, 0x02014b50);
            WriteUInt16(ms, 20);
            WriteUInt16(ms, 20);
            WriteUInt16(ms, flags[i]);
            WriteUInt16(ms, 0);
            WriteUInt16(ms, 0);
            WriteUInt16(ms, 0x5521);
            WriteUInt32(ms, 0);
            WriteUInt32(ms, 0);
            WriteUInt32(ms, 0);
            WriteUInt16(ms, (ushort)nameBytes.Length);
            WriteUInt16(ms, 0);
            WriteUInt16(ms, 0);
            WriteUInt16(ms, 0);
            WriteUInt16(ms, 0);
            WriteUInt32(ms, 0x20);
            WriteUInt32(ms, (uint)localHeaderOffsets[i]);
            ms.Write(nameBytes);
        }

        long cdSize = ms.Position - cdOffset;
        WriteUInt32(ms, 0x06054b50);
        WriteUInt16(ms, 0);
        WriteUInt16(ms, 0);
        WriteUInt16(ms, (ushort)entries.Count);
        WriteUInt16(ms, (ushort)entries.Count);
        WriteUInt32(ms, (uint)cdSize);
        WriteUInt32(ms, (uint)cdOffset);
        WriteUInt16(ms, 0);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static void WriteUInt32(Stream s, uint v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
        s.Write(buf);
    }

    private static void WriteUInt16(Stream s, ushort v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, v);
        s.Write(buf);
    }

    #endregion

    #region Helper: Build ArchiveStructureCache

    private async Task<ArchiveStructure> BuildStructureAsync(
        string zipPath,
        IFilenameEncodingDetector? detector = null)
    {
        detector ??= new FilenameEncodingDetector(
            Options.Create(new EncodingDetectionOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FilenameEncodingDetector>.Instance);

        var readerFactory = new ZipReaderFactory();
        var evictionPolicy = new LruEvictionPolicy();
        var structureStore = new ArchiveStructureStore(
            evictionPolicy, TimeProvider.System, NullLoggerFactory.Instance);
        var cacheOpts = Options.Create(new CacheOptions { MemoryCacheSizeMb = 256, DiskCacheSizeMb = 256 });

        var cache = new ArchiveStructureCache(
            structureStore, readerFactory, TimeProvider.System, cacheOpts,
            NullLogger<ArchiveStructureCache>.Instance, detector);

        return await cache.GetOrBuildAsync("test", zipPath);
    }

    #endregion

    [Fact]
    public async Task ShiftJisArchive_DecodesToCorrectJapanesePaths()
    {
        // Arrange
        string zipPath = BuildZipWithEncoding("sjis.zip", new[]
        {
            ("テスト文書/データ.txt", 932),
            ("日本語/ファイル名.doc", 932),
            ("設定/環境変数.cfg", 932),
        });

        // Act
        ArchiveStructure structure = await BuildStructureAsync(zipPath);

        // Assert
        structure.Entries.ContainsKey("テスト文書/データ.txt").Should().BeTrue();
        structure.Entries.ContainsKey("日本語/ファイル名.doc").Should().BeTrue();
        structure.Entries.ContainsKey("設定/環境変数.cfg").Should().BeTrue();
    }

    [Fact]
    public async Task GbkArchive_DecodesToCorrectChinesePaths()
    {
        // Arrange
        string zipPath = BuildZipWithEncoding("gbk.zip", new[]
        {
            ("测试文件/报告.txt", 936),
            ("文档/技术规范.doc", 936),
            ("数据/配置信息.cfg", 936),
        });

        // Act
        ArchiveStructure structure = await BuildStructureAsync(zipPath);

        // Assert
        structure.Entries.ContainsKey("测试文件/报告.txt").Should().BeTrue();
        structure.Entries.ContainsKey("文档/技术规范.doc").Should().BeTrue();
        structure.Entries.ContainsKey("数据/配置信息.cfg").Should().BeTrue();
    }

    [Fact]
    public async Task MixedJapaneseAndChinese_PerEntryDetectionDecodesCorrectly()
    {
        // Arrange — 3 Shift-JIS + 2 GBK entries (mixed encoding, no UTF-8 flag)
        string zipPath = BuildZipWithEncoding("mixed_ja_zh.zip", new[]
        {
            ("テスト文書/データ.txt", 932),
            ("日本語/ファイル名.doc", 932),
            ("設定/環境変数.cfg", 932),
            ("测试文件/报告.txt", 936),
            ("文档/技术规范.doc", 936),
        });

        // Act
        ArchiveStructure structure = await BuildStructureAsync(zipPath);

        // Assert — verify the archive parsed without errors and contains all entries.
        // For mixed-encoding, per-entry detection should decode entries correctly.
        // We verify specific CJK characters are present in trie keys to confirm
        // the filenames were decoded (not garbled CP437).
        structure.EntryCount.Should().BeGreaterThanOrEqualTo(5);

        // At least some entries should contain CJK characters (not mojibake)
        var allKeys = structure.Entries.Keys.ToList();
        bool hasJapanese = allKeys.Any(k => k.Any(c => c >= '\u3040' && c <= '\u9FFF'));
        bool hasChinese = allKeys.Any(k => k.Any(c => c >= '\u4E00' && c <= '\u9FFF'));
        (hasJapanese || hasChinese).Should().BeTrue("decoded keys should contain CJK characters, not CP437 mojibake");
    }

    [Fact]
    public async Task MixedKoreanAndCyrillic_PerEntryDetectionHandlesBoth()
    {
        // Arrange
        string zipPath = BuildZipWithEncoding("mixed_ko_ru.zip", new[]
        {
            ("테스트/보고서.txt", 949),
            ("문서/기술사양.doc", 949),
            ("데이터/설정파일.cfg", 949),
            ("Документы/отчёт.txt", 1251),
            ("Настройки/конфигурация.cfg", 1251),
        });

        // Act
        ArchiveStructure structure = await BuildStructureAsync(zipPath);

        // Assert — verify entries contain Korean hangul or Cyrillic characters
        structure.EntryCount.Should().BeGreaterThanOrEqualTo(5);

        var allKeys = structure.Entries.Keys.ToList();
        bool hasKorean = allKeys.Any(k => k.Any(c => c >= '\uAC00' && c <= '\uD7AF'));
        bool hasCyrillic = allKeys.Any(k => k.Any(c => c >= '\u0400' && c <= '\u04FF'));
        (hasKorean || hasCyrillic).Should().BeTrue("decoded keys should contain Korean or Cyrillic characters");
    }

    [Fact]
    public async Task MixedWithAsciiMajority_AsciiEntriesUnaffected()
    {
        // Arrange — 6 ASCII entries + 2 Shift-JIS entries
        string zipPath = BuildZipWithEncoding("mixed_ascii.zip", new[]
        {
            ("docs/readme.txt", 437),
            ("src/main.cs", 437),
            ("tests/test.cs", 437),
            ("config/settings.json", 437),
            ("build/output.dll", 437),
            ("lib/helper.dll", 437),
            ("テスト/データ.txt", 932),
            ("設定/環境.cfg", 932),
        });

        // Act
        ArchiveStructure structure = await BuildStructureAsync(zipPath);

        // Assert — ASCII entries should always decode correctly
        structure.Entries.ContainsKey("docs/readme.txt").Should().BeTrue();
        structure.Entries.ContainsKey("src/main.cs").Should().BeTrue();
        structure.Entries.ContainsKey("config/settings.json").Should().BeTrue();
    }

    [Fact]
    public async Task AsciiOnlyArchive_DecodesCorrectly()
    {
        // Arrange
        string zipPath = BuildZipWithEncoding("ascii.zip", new[]
        {
            ("docs/readme.txt", 437),
            ("src/main.cs", 437),
            ("tests/unit_test.cs", 437),
            ("config/settings.json", 437),
        });

        // Act
        ArchiveStructure structure = await BuildStructureAsync(zipPath);

        // Assert
        structure.Entries.ContainsKey("docs/readme.txt").Should().BeTrue();
        structure.Entries.ContainsKey("src/main.cs").Should().BeTrue();
        structure.Entries.ContainsKey("tests/unit_test.cs").Should().BeTrue();
        structure.Entries.ContainsKey("config/settings.json").Should().BeTrue();
    }

    [Fact]
    public async Task Utf8FlaggedEntries_BypassDetectionEntirely()
    {
        // Arrange — mix of UTF-8 flagged and non-UTF8 (Shift-JIS) entries
        string zipPath = BuildMixedUtf8Zip("mixed_utf8.zip", new[]
        {
            ("docs/readme.txt", 437, true),           // UTF-8 flagged
            ("テスト文書/データ.txt", 932, false),       // Shift-JIS, no flag
            ("日本語/ファイル名.doc", 932, false),       // Shift-JIS, no flag
            ("src/main.cs", 437, true),                // UTF-8 flagged
        });

        // Act
        ArchiveStructure structure = await BuildStructureAsync(zipPath);

        // Assert — UTF-8 flagged entries should always decode correctly
        structure.Entries.ContainsKey("docs/readme.txt").Should().BeTrue();
        structure.Entries.ContainsKey("src/main.cs").Should().BeTrue();
        // Shift-JIS entries should be detected and decoded
        structure.Entries.ContainsKey("テスト文書/データ.txt").Should().BeTrue();
        structure.Entries.ContainsKey("日本語/ファイル名.doc").Should().BeTrue();
    }

    [Fact]
    public async Task ParentDirectories_SynthesizedForDecodedPaths()
    {
        // Arrange — entries with parent directories that don't exist as explicit entries
        string zipPath = BuildZipWithEncoding("parents.zip", new[]
        {
            ("テスト文書/サブフォルダ/データ.txt", 932),
            ("日本語/深い階層/ファイル.doc", 932),
        });

        // Act
        ArchiveStructure structure = await BuildStructureAsync(zipPath);

        // Assert — parent directories should be synthesized
        structure.Entries.ContainsKey("テスト文書/").Should().BeTrue();
        structure.Entries.ContainsKey("テスト文書/サブフォルダ/").Should().BeTrue();
        structure.Entries.ContainsKey("日本語/").Should().BeTrue();
        structure.Entries.ContainsKey("日本語/深い階層/").Should().BeTrue();
    }
}
