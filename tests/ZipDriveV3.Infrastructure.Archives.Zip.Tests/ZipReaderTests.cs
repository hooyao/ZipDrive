using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Xunit;
using ZipDriveV3.Domain.Exceptions;
using ZipDriveV3.Domain.Models;
using ZipDriveV3.Infrastructure.Archives.Zip;
using ZipDriveV3.Infrastructure.Archives.Zip.Formats;

namespace ZipDriveV3.Infrastructure.Archives.Zip.Tests;

/// <summary>
/// Module initializer to register code pages encoding provider.
/// </summary>
file static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}

/// <summary>
/// Integration tests for ZipReader with real ZIP files.
/// </summary>
public class ZipReaderTests : IDisposable
{
    private readonly string _tempDir;

    public ZipReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ZipReaderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    #region EOCD Tests

    [Fact]
    public async Task ReadEocdAsync_ValidZip_ReturnsCorrectEntryCount()
    {
        // Arrange
        string zipPath = CreateTestZip("test.zip", new Dictionary<string, string>
        {
            ["file1.txt"] = "Hello World",
            ["file2.txt"] = "Goodbye World",
            ["folder/file3.txt"] = "Nested file"
        });

        // Act
        await using (ZipReader reader = new ZipReader(zipPath))
        {
            ZipEocd eocd = await reader.ReadEocdAsync();

            // Assert
            eocd.EntryCount.Should().Be(3);
            eocd.IsZip64.Should().BeFalse();
            eocd.CentralDirectoryOffset.Should().BeGreaterThan(0);
            eocd.CentralDirectorySize.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task ReadEocdAsync_EmptyZip_ReturnsZeroEntries()
    {
        // Arrange
        string zipPath = CreateTestZip("empty.zip", new Dictionary<string, string>());

        // Act
        await using (ZipReader reader = new ZipReader(zipPath))
        {
            ZipEocd eocd = await reader.ReadEocdAsync();

            // Assert
            eocd.EntryCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task ReadEocdAsync_InvalidFile_ThrowsEocdNotFoundException()
    {
        // Arrange
        string filePath = Path.Combine(_tempDir, "not_a_zip.txt");
        await File.WriteAllTextAsync(filePath, "This is not a ZIP file");

        // Act & Assert
        await using (ZipReader reader = new ZipReader(filePath))
        {
            await Assert.ThrowsAsync<EocdNotFoundException>(() => reader.ReadEocdAsync());
        }
    }

    [Fact]
    public async Task ReadEocdAsync_TooSmallFile_ThrowsCorruptZipException()
    {
        // Arrange
        string filePath = Path.Combine(_tempDir, "tiny.zip");
        await File.WriteAllBytesAsync(filePath, new byte[10]);

        // Act & Assert
        await using (ZipReader reader = new ZipReader(filePath))
        {
            await Assert.ThrowsAsync<CorruptZipException>(() => reader.ReadEocdAsync());
        }
    }

    #endregion

    #region Central Directory Streaming Tests

    [Fact]
    public async Task StreamCentralDirectoryAsync_ValidZip_YieldsAllEntries()
    {
        // Arrange
        Dictionary<string, string> files = new Dictionary<string, string>
        {
            ["file1.txt"] = "Content 1",
            ["file2.txt"] = "Content 2",
            ["folder/file3.txt"] = "Content 3",
            ["folder/subfolder/file4.txt"] = "Content 4"
        };
        string zipPath = CreateTestZip("test.zip", files);

        await using (ZipReader reader = new ZipReader(zipPath))
        {
            ZipEocd eocd = await reader.ReadEocdAsync();

            // Act
            List<ZipCentralDirectoryEntry> entries = new List<ZipCentralDirectoryEntry>();
            await foreach (ZipCentralDirectoryEntry entry in reader.StreamCentralDirectoryAsync(eocd))
            {
                entries.Add(entry);
            }

            // Assert
            entries.Should().HaveCount(4);
            entries.Select(e => e.FileName).Should().Contain("file1.txt");
            entries.Select(e => e.FileName).Should().Contain("file2.txt");
            entries.Select(e => e.FileName).Should().Contain("folder/file3.txt");
            entries.Select(e => e.FileName).Should().Contain("folder/subfolder/file4.txt");
        }
    }

    [Fact]
    public async Task StreamCentralDirectoryAsync_EmptyZip_YieldsNoEntries()
    {
        // Arrange
        string zipPath = CreateTestZip("empty.zip", new Dictionary<string, string>());

        await using (ZipReader reader = new ZipReader(zipPath))
        {
            ZipEocd eocd = await reader.ReadEocdAsync();

            // Act
            int count = 0;
            await foreach (ZipCentralDirectoryEntry _ in reader.StreamCentralDirectoryAsync(eocd))
            {
                count++;
            }

            // Assert
            count.Should().Be(0);
        }
    }

    [Fact]
    public async Task StreamCentralDirectoryAsync_LargeZip_StreamsEfficiently()
    {
        // Arrange - Create ZIP with many entries
        Dictionary<string, string> files = Enumerable.Range(1, 100)
            .ToDictionary(i => $"file{i}.txt", i => $"Content {i}");
        string zipPath = CreateTestZip("large.zip", files);

        await using (ZipReader reader = new ZipReader(zipPath))
        {
            ZipEocd eocd = await reader.ReadEocdAsync();

            // Act - Stream and count without loading all into memory
            int count = 0;
            long totalUncompressedSize = 0L;
            await foreach (ZipCentralDirectoryEntry entry in reader.StreamCentralDirectoryAsync(eocd))
            {
                count++;
                totalUncompressedSize += entry.UncompressedSize;
            }

            // Assert
            count.Should().Be(100);
            totalUncompressedSize.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task StreamCentralDirectoryAsync_Cancellation_StopsEnumeration()
    {
        // Arrange
        Dictionary<string, string> files = Enumerable.Range(1, 100)
            .ToDictionary(i => $"file{i}.txt", i => $"Content {i}");
        string zipPath = CreateTestZip("large.zip", files);

        await using (ZipReader reader = new ZipReader(zipPath))
        {
            ZipEocd eocd = await reader.ReadEocdAsync();
            CancellationTokenSource cts = new CancellationTokenSource();

            // Act
            int count = 0;
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (ZipCentralDirectoryEntry _ in reader.StreamCentralDirectoryAsync(eocd, cts.Token))
                {
                    count++;
                    if (count == 10)
                    {
                        cts.Cancel();
                    }
                }
            });

            // Assert
            count.Should().Be(10);
        }
    }

    [Fact]
    public async Task StreamCentralDirectoryAsync_DirectoryEntry_HasIsDirectoryTrue()
    {
        // Arrange
        string zipPath = CreateTestZipWithDirectory("with_dir.zip");

        await using (ZipReader reader = new ZipReader(zipPath))
        {
            ZipEocd eocd = await reader.ReadEocdAsync();

            // Act
            List<ZipCentralDirectoryEntry> entries = new List<ZipCentralDirectoryEntry>();
            await foreach (ZipCentralDirectoryEntry entry in reader.StreamCentralDirectoryAsync(eocd))
            {
                entries.Add(entry);
            }

            // Assert
            ZipCentralDirectoryEntry dirEntry = entries.FirstOrDefault(e => e.FileName.EndsWith("/"));
            dirEntry.IsDirectory.Should().BeTrue();
        }
    }

    #endregion

    #region Local Header and Extraction Tests

    [Fact]
    public async Task ReadLocalHeaderAsync_ValidEntry_ReturnsCorrectHeaderSize()
    {
        // Arrange
        string zipPath = CreateTestZip("test.zip", new Dictionary<string, string>
        {
            ["file.txt"] = "Hello"
        });

        await using (ZipReader reader = new ZipReader(zipPath))
        {
            ZipEocd eocd = await reader.ReadEocdAsync();

            ZipCentralDirectoryEntry? cdEntry = null;
            await foreach (ZipCentralDirectoryEntry entry in reader.StreamCentralDirectoryAsync(eocd))
            {
                cdEntry = entry;
                break;
            }

            // Act
            ZipLocalHeader localHeader = await reader.ReadLocalHeaderAsync(cdEntry!.Value.LocalHeaderOffset);

            // Assert
            localHeader.FileNameLength.Should().Be((ushort)"file.txt".Length);
            localHeader.TotalHeaderSize.Should().BeGreaterThanOrEqualTo(30 + 8); // 30 fixed + filename
        }
    }

    [Fact]
    public async Task OpenEntryStreamAsync_StoreCompression_ExtractsCorrectly()
    {
        // Arrange
        string content = "Hello World!";
        string zipPath = CreateTestZip("store.zip", new Dictionary<string, string>
        {
            ["file.txt"] = content
        }, CompressionLevel.NoCompression);

        await using (ZipReader reader = new ZipReader(zipPath))
        {
            ZipEocd eocd = await reader.ReadEocdAsync();

            ZipCentralDirectoryEntry? cdEntry = null;
            await foreach (ZipCentralDirectoryEntry entry in reader.StreamCentralDirectoryAsync(eocd))
            {
                cdEntry = entry;
                break;
            }

            ZipEntryInfo entryInfo = new Domain.Models.ZipEntryInfo
            {
                LocalHeaderOffset = cdEntry!.Value.LocalHeaderOffset,
                CompressedSize = cdEntry.Value.CompressedSize,
                UncompressedSize = cdEntry.Value.UncompressedSize,
                CompressionMethod = cdEntry.Value.CompressionMethod,
                IsDirectory = false,
                LastModified = DateTime.Now,
                Attributes = FileAttributes.Normal
            };

            // Act
            await using (Stream stream = await reader.OpenEntryStreamAsync(entryInfo))
            {
                using (StreamReader sr = new StreamReader(stream))
                {
                    string extracted = await sr.ReadToEndAsync();

                    // Assert
                    extracted.Should().Be(content);
                }
            }
        }
    }

    [Fact]
    public async Task OpenEntryStreamAsync_DeflateCompression_ExtractsCorrectly()
    {
        // Arrange
        string content = "Hello World! This is a longer string that will be compressed with Deflate.";
        string zipPath = CreateTestZip("deflate.zip", new Dictionary<string, string>
        {
            ["file.txt"] = content
        }, CompressionLevel.Optimal);

        await using (ZipReader reader = new ZipReader(zipPath))
        {
            ZipEocd eocd = await reader.ReadEocdAsync();

            ZipCentralDirectoryEntry? cdEntry = null;
            await foreach (ZipCentralDirectoryEntry entry in reader.StreamCentralDirectoryAsync(eocd))
            {
                cdEntry = entry;
                break;
            }

            ZipEntryInfo entryInfo = new Domain.Models.ZipEntryInfo
            {
                LocalHeaderOffset = cdEntry!.Value.LocalHeaderOffset,
                CompressedSize = cdEntry.Value.CompressedSize,
                UncompressedSize = cdEntry.Value.UncompressedSize,
                CompressionMethod = cdEntry.Value.CompressionMethod,
                IsDirectory = false,
                LastModified = DateTime.Now,
                Attributes = FileAttributes.Normal
            };

            // Act
            await using (Stream stream = await reader.OpenEntryStreamAsync(entryInfo))
            {
                using (StreamReader sr = new StreamReader(stream))
                {
                    string extracted = await sr.ReadToEndAsync();

                    // Assert
                    extracted.Should().Be(content);
                }
            }
        }
    }

    #endregion

    #region SubStream Tests

    [Fact]
    public async Task SubStream_BoundedRead_DoesNotReadBeyondBounds()
    {
        // Arrange
        byte[] data = new byte[100];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)i;

        MemoryStream baseStream = new MemoryStream(data);
        baseStream.Position = 10; // Start at offset 10

        SubStream subStream = new SubStream(baseStream, 20); // Read only 20 bytes

        // Act
        byte[] buffer = new byte[50];
        int bytesRead = await subStream.ReadAsync(buffer.AsMemory());

        // Assert
        bytesRead.Should().Be(20);
        buffer[0].Should().Be(10);
        buffer[19].Should().Be(29);
    }

    [Fact]
    public void SubStream_Seek_StaysWithinBounds()
    {
        // Arrange
        byte[] data = new byte[100];
        MemoryStream baseStream = new MemoryStream(data);
        baseStream.Position = 10;

        SubStream subStream = new SubStream(baseStream, 20);

        // Act & Assert
        subStream.Seek(5, SeekOrigin.Begin).Should().Be(5);
        subStream.Position.Should().Be(5);

        subStream.Seek(10, SeekOrigin.Current).Should().Be(15);
        subStream.Position.Should().Be(15);

        subStream.Seek(-5, SeekOrigin.End).Should().Be(15);
        subStream.Position.Should().Be(15);
    }

    [Fact]
    public void SubStream_SeekBeyondBounds_Throws()
    {
        // Arrange
        byte[] data = new byte[100];
        MemoryStream baseStream = new MemoryStream(data);
        baseStream.Position = 10;

        SubStream subStream = new SubStream(baseStream, 20);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => subStream.Seek(25, SeekOrigin.Begin));
        Assert.Throws<ArgumentOutOfRangeException>(() => subStream.Seek(-5, SeekOrigin.Begin));
    }

    #endregion

    #region Helper Methods

    private string CreateTestZip(
        string fileName,
        Dictionary<string, string> files,
        CompressionLevel compressionLevel = CompressionLevel.Fastest)
    {
        string zipPath = Path.Combine(_tempDir, fileName);

        using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach ((string name, string content) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, compressionLevel);
                using (StreamWriter writer = new StreamWriter(entry.Open()))
                {
                    writer.Write(content);
                }
            }
        }

        return zipPath;
    }

    private string CreateTestZipWithDirectory(string fileName)
    {
        string zipPath = Path.Combine(_tempDir, fileName);

        using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            // Create a directory entry (empty entry with trailing slash)
            archive.CreateEntry("mydir/");

            // Create a file inside the directory
            ZipArchiveEntry entry = archive.CreateEntry("mydir/file.txt");
            using (StreamWriter writer = new StreamWriter(entry.Open()))
            {
                writer.Write("File in directory");
            }
        }

        return zipPath;
    }

    #endregion
}
