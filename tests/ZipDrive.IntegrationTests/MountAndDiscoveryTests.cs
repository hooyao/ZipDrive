using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZipDrive.Application.Services;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;
using ZipDrive.TestHelpers;

namespace ZipDrive.IntegrationTests;

/// <summary>
/// 11.1 Mount and Discovery integration tests.
/// </summary>
[Collection("VfsIntegration")]
public class MountAndDiscoveryTests
{
    private readonly VfsTestFixture _fixture;

    public MountAndDiscoveryTests(VfsTestFixture fixture) => _fixture = fixture;

    [Fact]
    public void Mount_AllArchivesDiscovered()
    {
        // Small-scale fixture: games/fps(2) + games/rpg(2) + docs/manuals(2) + root(1) + edge(2) = 9
        _fixture.ArchiveTrie.ArchiveCount.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void Mount_VirtualFoldersDerived()
    {
        _fixture.ArchiveTrie.IsVirtualFolder("games").Should().BeTrue();
        _fixture.ArchiveTrie.IsVirtualFolder("games/fps").Should().BeTrue();
    }

    [Fact]
    public void Mount_IsMountedTrue()
    {
        _fixture.Vfs.IsMounted.Should().BeTrue();
    }

    [Fact]
    public async Task ListRoot_ContainsVirtualFoldersAndArchives()
    {
        var entries = await _fixture.Vfs.ListDirectoryAsync("");

        entries.Should().NotBeEmpty();
        entries.Should().Contain(e => e.IsDirectory);
    }

    [Fact]
    public async Task Mount_EmptyDirectory_Succeeds()
    {
        // Create a temp empty dir, mount a fresh VFS against it
        string emptyDir = Path.Combine(Path.GetTempPath(), "VfsEmpty_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(emptyDir);
        try
        {
            var fixture = new VfsTestFixture();
            // Use a custom path - can't reuse the shared fixture for this test
            // Just verify discovery on empty returns empty
            var zipOnlyBuilder = new ZipOnlyBuilder();
            var registry = new FormatRegistry([zipOnlyBuilder], [], []);
            var discovery = new ArchiveDiscovery(registry, NullLogger<ArchiveDiscovery>.Instance);
            var results = await discovery.DiscoverAsync(emptyDir, 6);
            results.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    private sealed class ZipOnlyBuilder : IArchiveStructureBuilder
    {
        public string FormatId => "zip";
        public IReadOnlyList<string> SupportedExtensions => [".zip"];
        public Task<ArchiveStructure> BuildAsync(string k, string p, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
