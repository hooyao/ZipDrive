using FluentAssertions;
using Xunit;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip;

namespace ZipDrive.Infrastructure.Archives.Zip.Tests;

public class ZipEntryExtractorTests
{
    private static readonly string TestFixturePath =
        Path.Combine(AppContext.BaseDirectory, "TestFixtures", "small.zip");

    [Fact]
    public async Task ExtractAsync_returns_decompressed_stream()
    {
        if (!File.Exists(TestFixturePath))
            return; // Skip if test fixture not available

        // Build structure first to populate metadata store
        var metadataStore = new ZipFormatMetadataStore();
        var readerFactory = new ZipReaderFactory();

        // Manually populate store by reading the archive
        await using var reader = readerFactory.Create(TestFixturePath);
        var eocd = await reader.ReadEocdAsync();
        var entries = new List<(string, ZipEntryInfo)>();
        await foreach (var cd in reader.StreamCentralDirectoryAsync(eocd))
        {
            if (!cd.IsDirectory)
            {
                var info = new ZipEntryInfo
                {
                    LocalHeaderOffset = cd.LocalHeaderOffset,
                    CompressedSize = cd.CompressedSize,
                    UncompressedSize = cd.UncompressedSize,
                    CompressionMethod = cd.CompressionMethod,
                    IsDirectory = false,
                    LastModified = cd.LastModified,
                    Attributes = FileAttributes.Normal,
                    Crc32 = cd.Crc32
                };
                entries.Add((cd.DecodeFileName().Replace('\\', '/'), info));
            }
        }

        if (entries.Count == 0) return;

        metadataStore.Populate(TestFixturePath, entries);

        // Test extraction
        var extractor = new ZipEntryExtractor(readerFactory, metadataStore);
        var (path, zipEntry) = entries[0];

        ExtractionResult result = await extractor.ExtractAsync(TestFixturePath, path);

        result.Stream.Should().NotBeNull();
        result.SizeBytes.Should().Be(zipEntry.UncompressedSize);
        result.OnDisposed.Should().NotBeNull();

        // Verify stream contains data
        byte[] buffer = new byte[result.SizeBytes];
        int read = await result.Stream.ReadAsync(buffer);
        read.Should().Be((int)result.SizeBytes);

        // Cleanup
        if (result.OnDisposed != null)
            await result.OnDisposed();
    }

    [Fact]
    public async Task ExtractAsync_missing_entry_throws()
    {
        var metadataStore = new ZipFormatMetadataStore();
        metadataStore.Populate("archive.zip", []);

        var extractor = new ZipEntryExtractor(new ZipReaderFactory(), metadataStore);

        Func<Task> act = () => extractor.ExtractAsync("archive.zip", "nonexistent.txt");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }
}
