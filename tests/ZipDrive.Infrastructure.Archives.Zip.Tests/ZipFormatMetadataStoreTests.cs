using FluentAssertions;
using Xunit;

namespace ZipDrive.Infrastructure.Archives.Zip.Tests;

public class ZipFormatMetadataStoreTests
{
    private readonly ZipFormatMetadataStore _store = new();

    private static ZipEntryInfo MakeEntry(long offset = 100, long compressed = 50, long uncompressed = 100) =>
        new()
        {
            LocalHeaderOffset = offset,
            CompressedSize = compressed,
            UncompressedSize = uncompressed,
            CompressionMethod = 8,
            IsDirectory = false,
            LastModified = DateTime.UtcNow,
            Attributes = FileAttributes.Normal
        };

    [Fact]
    public void Populate_and_Get_returns_entry()
    {
        var entry = MakeEntry(offset: 500);
        _store.Populate("archive.zip", [("folder/file.txt", entry)]);

        ZipEntryInfo result = _store.Get("archive.zip", "folder/file.txt");

        result.LocalHeaderOffset.Should().Be(500);
    }

    [Fact]
    public void Get_missing_archive_throws()
    {
        Action act = () => _store.Get("missing.zip", "file.txt");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Get_missing_entry_throws()
    {
        _store.Populate("archive.zip", [("a.txt", MakeEntry())]);

        Action act = () => _store.Get("archive.zip", "missing.txt");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Remove_clears_entries()
    {
        _store.Populate("archive.zip", [("file.txt", MakeEntry())]);
        _store.Remove("archive.zip");

        Action act = () => _store.Get("archive.zip", "file.txt");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Populate_replaces_existing_entries()
    {
        _store.Populate("archive.zip", [("old.txt", MakeEntry(offset: 100))]);
        _store.Populate("archive.zip", [("new.txt", MakeEntry(offset: 200))]);

        Action getOld = () => _store.Get("archive.zip", "old.txt");
        getOld.Should().Throw<FileNotFoundException>();

        _store.Get("archive.zip", "new.txt").LocalHeaderOffset.Should().Be(200);
    }

    [Fact]
    public void GetArchiveEntries_returns_all_entries()
    {
        _store.Populate("archive.zip", [
            ("a.txt", MakeEntry(offset: 100)),
            ("b.txt", MakeEntry(offset: 200))
        ]);

        var entries = _store.GetArchiveEntries("archive.zip");
        entries.Should().NotBeNull();
        entries!.Count.Should().Be(2);
    }

    [Fact]
    public void GetArchiveEntries_returns_null_for_missing()
    {
        _store.GetArchiveEntries("missing.zip").Should().BeNull();
    }

    [Fact]
    public void Remove_nonexistent_is_noop()
    {
        _store.Remove("nonexistent.zip"); // Should not throw
        _store.ArchiveCount.Should().Be(0);
    }

    [Fact]
    public void Thread_safety_concurrent_populate_and_get()
    {
        // Populate from multiple threads, get from multiple threads
        Parallel.For(0, 100, i =>
        {
            string key = $"archive{i}.zip";
            _store.Populate(key, [($"file{i}.txt", MakeEntry(offset: i * 100))]);
            ZipEntryInfo entry = _store.Get(key, $"file{i}.txt");
            entry.LocalHeaderOffset.Should().Be(i * 100);
        });

        _store.ArchiveCount.Should().Be(100);
    }
}
