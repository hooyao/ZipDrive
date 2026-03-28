using FluentAssertions;
using ZipDrive.Application.Services;
using ZipDrive.Domain;
using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Tests;

public class ArchiveTrieTests
{
    private static ArchiveDescriptor MakeArchive(string virtualPath) => new()
    {
        VirtualPath = virtualPath,
        PhysicalPath = $@"D:\Archives\{virtualPath.Replace('/', '\\')}",
        SizeBytes = 1024,
        LastModifiedUtc = DateTime.UtcNow
    };

    // === Registration and lookup ===

    [Fact]
    public void AddArchive_SingleArchive_CanBeFound()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("backup.zip"));

        trie.ArchiveCount.Should().Be(1);
        trie.Archives.Should().ContainSingle(a => a.VirtualPath == "backup.zip");
    }

    [Fact]
    public void AddArchive_MultipleArchives_AllFound()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("games/doom.zip"));
        trie.AddArchive(MakeArchive("games/quake.zip"));
        trie.AddArchive(MakeArchive("docs/manuals.zip"));

        trie.ArchiveCount.Should().Be(3);
    }

    [Fact]
    public void RemoveArchive_ExistingArchive_ReturnsTrue()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("backup.zip"));

        trie.RemoveArchive("backup.zip").Should().BeTrue();
        trie.ArchiveCount.Should().Be(0);
    }

    [Fact]
    public void RemoveArchive_NonExistent_ReturnsFalse()
    {
        var trie = new ArchiveTrie();
        trie.RemoveArchive("nonexistent.zip").Should().BeFalse();
    }

    // === Resolve: all status values ===

    [Fact]
    public void Resolve_EmptyPath_ReturnsVirtualRoot()
    {
        var trie = new ArchiveTrie();
        var result = trie.Resolve("");

        result.Status.Should().Be(ArchiveTrieStatus.VirtualRoot);
    }

    [Fact]
    public void Resolve_InsideArchive_ReturnsInsideArchiveWithInternalPath()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("games/doom.zip"));

        var result = trie.Resolve("games/doom.zip/maps/e1m1.wad");

        result.Status.Should().Be(ArchiveTrieStatus.InsideArchive);
        result.Archive!.VirtualPath.Should().Be("games/doom.zip");
        result.InternalPath.Should().Be("maps/e1m1.wad");
    }

    [Fact]
    public void Resolve_ArchiveRoot_ReturnsArchiveRoot()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("games/doom.zip"));

        var result = trie.Resolve("games/doom.zip");

        result.Status.Should().Be(ArchiveTrieStatus.ArchiveRoot);
        result.Archive!.VirtualPath.Should().Be("games/doom.zip");
        result.InternalPath.Should().Be("");
    }

    [Fact]
    public void Resolve_ArchiveRootWithTrailingSlash_ReturnsArchiveRoot()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("games/doom.zip"));

        var result = trie.Resolve("games/doom.zip/");

        result.Status.Should().Be(ArchiveTrieStatus.ArchiveRoot);
    }

    [Fact]
    public void Resolve_VirtualFolder_ReturnsVirtualFolder()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("games/doom.zip"));

        var result = trie.Resolve("games");

        result.Status.Should().Be(ArchiveTrieStatus.VirtualFolder);
        result.VirtualFolderPath.Should().Be("games");
    }

    [Fact]
    public void Resolve_NotFound_ReturnsNotFound()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("games/doom.zip"));

        var result = trie.Resolve("nonexistent/path");

        result.Status.Should().Be(ArchiveTrieStatus.NotFound);
    }

    // === Virtual folder derivation ===

    [Fact]
    public void IsVirtualFolder_AncestorOfArchive_ReturnsTrue()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("games/doom.zip"));

        trie.IsVirtualFolder("games").Should().BeTrue();
    }

    [Fact]
    public void IsVirtualFolder_NestedAncestors_AllRegistered()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("a/b/c/data.zip"));

        trie.IsVirtualFolder("a").Should().BeTrue();
        trie.IsVirtualFolder("a/b").Should().BeTrue();
        trie.IsVirtualFolder("a/b/c").Should().BeTrue();
    }

    [Fact]
    public void IsVirtualFolder_RootLevelArchive_NoVirtualFolders()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("backup.zip"));

        trie.IsVirtualFolder("backup.zip").Should().BeFalse();
    }

    // === Folder listing ===

    [Fact]
    public void ListFolder_Root_ReturnsMixedContent()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("games/doom.zip"));
        trie.AddArchive(MakeArchive("docs/manuals.zip"));
        trie.AddArchive(MakeArchive("backup.zip"));

        var entries = trie.ListFolder("").ToList();

        entries.Should().Contain(e => e.Name == "games" && !e.IsArchive);
        entries.Should().Contain(e => e.Name == "docs" && !e.IsArchive);
        entries.Should().Contain(e => e.Name == "backup.zip" && e.IsArchive);
    }

    [Fact]
    public void ListFolder_Subfolder_ReturnsArchivesOnly()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("games/doom.zip"));
        trie.AddArchive(MakeArchive("games/quake.zip"));

        var entries = trie.ListFolder("games").ToList();

        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.IsArchive);
    }

    [Fact]
    public void ListFolder_MixedArchivesAndSubfolders()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("games/doom.zip"));
        trie.AddArchive(MakeArchive("games/retro/duke.zip"));

        var entries = trie.ListFolder("games").ToList();

        entries.Should().Contain(e => e.Name == "doom.zip" && e.IsArchive);
        entries.Should().Contain(e => e.Name == "retro" && !e.IsArchive);
        entries.Should().NotContain(e => e.Name == "duke.zip"); // Nested, not direct child
    }

    // === Case sensitivity ===

    [Fact]
    public void Resolve_CaseInsensitive_MatchesDifferentCase()
    {
        var trie = new ArchiveTrie(CaseInsensitiveCharComparer.Instance);
        trie.AddArchive(MakeArchive("games/doom.zip"));

        var result = trie.Resolve("GAMES/DOOM.ZIP/maps/e1m1.wad");

        result.Status.Should().Be(ArchiveTrieStatus.InsideArchive);
        result.Archive!.VirtualPath.Should().Be("games/doom.zip");
    }

    [Fact]
    public void Resolve_CaseSensitive_DoesNotMatchDifferentCase()
    {
        var trie = new ArchiveTrie(); // Default = case-sensitive
        trie.AddArchive(MakeArchive("games/doom.zip"));

        var result = trie.Resolve("GAMES/DOOM.ZIP/maps/e1m1.wad");

        result.Status.Should().Be(ArchiveTrieStatus.NotFound);
    }

    // === Thread safety ===

    // TC-AT-01: Concurrent Resolve during AddArchive
    [Fact]
    public async Task ConcurrentResolve_DuringAddArchive_NoCorruption()
    {
        var trie = new ArchiveTrie();
        for (int i = 0; i < 5; i++)
            trie.AddArchive(MakeArchive($"archive{i}.zip"));

        const int readers = 10;
        const int iterations = 1000;
        var barrier = new Barrier(readers + 1);
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Reader threads
        var readerTasks = Enumerable.Range(0, readers).Select(r => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < iterations; i++)
            {
                var result = trie.Resolve($"archive{i % 5}.zip");
                if (result.Status != ArchiveTrieStatus.ArchiveRoot)
                    errors.Add($"Reader {r}, iter {i}: expected ArchiveRoot, got {result.Status}");

                var notFound = trie.Resolve("nonexistent.zip");
                if (notFound.Status != ArchiveTrieStatus.NotFound)
                    errors.Add($"Reader {r}, iter {i}: expected NotFound, got {notFound.Status}");
            }
        })).ToArray();

        // Writer thread
        var writerTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            trie.AddArchive(MakeArchive("newarchive.zip"));
        });

        await Task.WhenAll([.. readerTasks, writerTask]);

        errors.Should().BeEmpty();
        trie.Resolve("newarchive.zip").Status.Should().Be(ArchiveTrieStatus.ArchiveRoot);
    }

    // TC-AT-02: Concurrent Resolve during RemoveArchive
    [Fact]
    public async Task ConcurrentResolve_DuringRemoveArchive_NoCorruption()
    {
        var trie = new ArchiveTrie();
        for (int i = 0; i < 5; i++)
            trie.AddArchive(MakeArchive($"archive{i}.zip"));

        const int readers = 10;
        const int iterations = 1000;
        var barrier = new Barrier(readers + 1);
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        var readerTasks = Enumerable.Range(0, readers).Select(r => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < iterations; i++)
            {
                // Resolve archives 0-3 (not 4, which will be removed)
                var result = trie.Resolve($"archive{i % 4}.zip");
                if (result.Status != ArchiveTrieStatus.ArchiveRoot)
                    errors.Add($"Reader {r}, iter {i}: expected ArchiveRoot for archive{i % 4}.zip, got {result.Status}");
            }
        })).ToArray();

        var writerTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            trie.RemoveArchive("archive4.zip");
        });

        await Task.WhenAll([.. readerTasks, writerTask]);

        errors.Should().BeEmpty();
        trie.Resolve("archive4.zip").Status.Should().Be(ArchiveTrieStatus.NotFound);
        // Archives 0-3 still there
        for (int i = 0; i < 4; i++)
            trie.Resolve($"archive{i}.zip").Status.Should().Be(ArchiveTrieStatus.ArchiveRoot);
    }

    // TC-AT-03: RemoveArchive cleans up virtual folders
    [Fact]
    public void RemoveArchive_CleansUpVirtualFolders()
    {
        var trie = new ArchiveTrie();
        trie.AddArchive(MakeArchive("games/doom.zip"));
        trie.AddArchive(MakeArchive("games/quake.zip"));

        trie.IsVirtualFolder("games").Should().BeTrue();

        trie.RemoveArchive("games/doom.zip");
        trie.IsVirtualFolder("games").Should().BeTrue(); // quake still there

        trie.RemoveArchive("games/quake.zip");
        trie.IsVirtualFolder("games").Should().BeFalse(); // no archives left
    }

    // TC-AT-04: RemoveArchive returns false for unknown
    [Fact]
    public void RemoveArchive_Unknown_ReturnsFalse()
    {
        var trie = new ArchiveTrie();
        trie.RemoveArchive("nosuch.zip").Should().BeFalse();
    }

    // TC-EDGE-13: Write lock duration under load
    [Fact]
    public void RemoveArchive_WriteLockDuration_Under10ms()
    {
        var trie = new ArchiveTrie();
        for (int i = 0; i < 1000; i++)
            trie.AddArchive(MakeArchive($"dir{i % 50}/archive{i}.zip"));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        trie.RemoveArchive("dir0/archive0.zip");
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(10);
    }
}
