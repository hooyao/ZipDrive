using System.Text;
using FluentAssertions;
using Xunit;
using ZipDrive.Infrastructure.Archives.Zip;

namespace ZipDrive.Infrastructure.Archives.Zip.Tests;

public class FilenameEncodingDetectorTests
{
    private readonly FilenameEncodingDetector _detector = new(confidenceThreshold: 0.5f);

    private static byte[] EncodeString(string text, int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage).GetBytes(text);
    }

    [Fact]
    public void DetectEncoding_ShiftJisBytes_DetectsCP932()
    {
        // Arrange — generic Japanese test filenames
        var filenames = new List<byte[]>
        {
            EncodeString("テスト文書/データ.txt", 932),
            EncodeString("日本語/ファイル名.doc", 932),
            EncodeString("設定/環境変数.cfg", 932),
            EncodeString("画像/写真一覧.jpg", 932),
            EncodeString("報告書/月次レポート.xlsx", 932),
        };

        // Act
        DetectionResult? result = _detector.DetectEncoding(filenames);

        // Assert
        result.Should().NotBeNull();
        result!.Confidence.Should().BeGreaterThanOrEqualTo(0.5f);
        // Shift-JIS is reported as various names (shift_jis, x-cp20932, etc.)
        // Verify it decodes correctly
        string decoded = result.Encoding.GetString(filenames[0]);
        decoded.Should().Be("テスト文書/データ.txt");
    }

    [Fact]
    public void DetectEncoding_GbkBytes_DetectsGbk()
    {
        // Arrange — generic Chinese test filenames
        var filenames = new List<byte[]>
        {
            EncodeString("测试文件/报告.txt", 936),
            EncodeString("文档/技术规范.doc", 936),
            EncodeString("数据/配置信息.cfg", 936),
            EncodeString("图片/产品目录.jpg", 936),
            EncodeString("模板/项目计划书.xlsx", 936),
        };

        // Act
        DetectionResult? result = _detector.DetectEncoding(filenames);

        // Assert
        result.Should().NotBeNull();
        result!.Confidence.Should().BeGreaterThanOrEqualTo(0.5f);
        string decoded = result.Encoding.GetString(filenames[0]);
        decoded.Should().Be("测试文件/报告.txt");
    }

    [Fact]
    public void DetectEncoding_EucKrBytes_DetectsEucKr()
    {
        // Arrange — generic Korean test filenames
        var filenames = new List<byte[]>
        {
            EncodeString("테스트/보고서.txt", 949),
            EncodeString("문서/기술사양.doc", 949),
            EncodeString("데이터/설정파일.cfg", 949),
            EncodeString("이미지/제품목록.jpg", 949),
            EncodeString("서식/프로젝트계획.xlsx", 949),
        };

        // Act
        DetectionResult? result = _detector.DetectEncoding(filenames);

        // Assert
        result.Should().NotBeNull();
        result!.Confidence.Should().BeGreaterThanOrEqualTo(0.5f);
        string decoded = result.Encoding.GetString(filenames[0]);
        decoded.Should().Be("테스트/보고서.txt");
    }

    [Fact]
    public void DetectEncoding_CyrillicBytes_DetectsWindows1251()
    {
        // Arrange — generic Cyrillic test filenames
        var filenames = new List<byte[]>
        {
            EncodeString("Документы/отчёт.txt", 1251),
            EncodeString("Настройки/конфигурация.cfg", 1251),
            EncodeString("Шаблоны/техническое задание.doc", 1251),
            EncodeString("Данные/результаты тестирования.xlsx", 1251),
            EncodeString("Изображения/фотографии продуктов.jpg", 1251),
        };

        // Act
        DetectionResult? result = _detector.DetectEncoding(filenames);

        // Assert
        result.Should().NotBeNull();
        result!.Confidence.Should().BeGreaterThanOrEqualTo(0.5f);
        string decoded = result.Encoding.GetString(filenames[0]);
        decoded.Should().Be("Документы/отчёт.txt");
    }

    [Fact]
    public void DetectEncoding_EmptyList_ReturnsNull()
    {
        // Act
        DetectionResult? result = _detector.DetectEncoding(new List<byte[]>());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void DetectEncoding_AsciiOnly_ReturnsCompatibleResult()
    {
        // Arrange — pure ASCII filenames
        var filenames = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("docs/readme.txt"),
            Encoding.ASCII.GetBytes("src/main.cs"),
            Encoding.ASCII.GetBytes("tests/unit_test.cs"),
            Encoding.ASCII.GetBytes("config/settings.json"),
            Encoding.ASCII.GetBytes("build/output.dll"),
        };

        // Act
        DetectionResult? result = _detector.DetectEncoding(filenames);

        // Assert — any result (including null) is acceptable for ASCII,
        // since ASCII is a subset of all supported encodings.
        // If a result is returned, it should decode ASCII correctly.
        if (result != null)
        {
            string decoded = result.Encoding.GetString(filenames[0]);
            decoded.Should().Be("docs/readme.txt");
        }
    }

    [Fact]
    public void DetectEncoding_HighThreshold_RejectsLowConfidence()
    {
        // Arrange — detector with very high threshold
        var strictDetector = new FilenameEncodingDetector(confidenceThreshold: 0.99f);
        var filenames = new List<byte[]>
        {
            EncodeString("テスト.txt", 932),
            EncodeString("データ.doc", 932),
        };

        // Act
        DetectionResult? result = strictDetector.DetectEncoding(filenames);

        // Assert — with 0.99 threshold, most detections will be rejected
        // (confidence is typically 0.5-0.95)
        if (result != null)
        {
            result.Confidence.Should().BeGreaterThanOrEqualTo(0.99f);
        }
    }

    [Fact]
    public void DetectSingleEntry_SufficientJapaneseFilename_Detects()
    {
        // Arrange — a single filename with enough bytes for detection
        byte[] filename = EncodeString("テスト文書/データファイル/設定情報.txt", 932);

        // Act
        DetectionResult? result = _detector.DetectSingleEntry(filename);

        // Assert
        result.Should().NotBeNull();
        string decoded = result!.Encoding.GetString(filename);
        decoded.Should().Be("テスト文書/データファイル/設定情報.txt");
    }
}
