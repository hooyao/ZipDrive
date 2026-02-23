using FluentAssertions;
using ZipDriveV3.Application.Services;
using ZipDriveV3.Domain.Models;

namespace ZipDriveV3.Domain.Tests;

public class ArchiveDiscoveryTests : IDisposable
{
    private readonly string _tempRoot;

    public ArchiveDiscoveryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ZipDriveTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private void CreateZip(string relativePath)
    {
        string fullPath = Path.Combine(_tempRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // Create a minimal valid ZIP (empty archive: EOCD record only)
        byte[] emptyZip = [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        File.WriteAllBytes(fullPath, emptyZip);
    }

    // === Depth limiting ===

    [Fact]
    public async Task DiscoverAsync_Depth1_OnlyFindsRootLevelZips()
    {
        CreateZip("root.zip");
        CreateZip("sub/nested.zip");

        var discovery = new ArchiveDiscovery();
        var results = await discovery.DiscoverAsync(_tempRoot, maxDepth: 1);

        results.Should().ContainSingle(a => a.VirtualPath == "root.zip");
    }

    [Fact]
    public async Task DiscoverAsync_Depth2_FindsOneLevel()
    {
        CreateZip("root.zip");
        CreateZip("sub/nested.zip");
        CreateZip("sub/deep/deeper.zip");

        var discovery = new ArchiveDiscovery();
        var results = await discovery.DiscoverAsync(_tempRoot, maxDepth: 2);

        results.Should().Contain(a => a.VirtualPath == "root.zip");
        results.Should().Contain(a => a.VirtualPath == "sub/nested.zip");
        results.Should().NotContain(a => a.VirtualPath == "sub/deep/deeper.zip");
    }

    [Fact]
    public async Task DiscoverAsync_DepthClampedToMin()
    {
        CreateZip("root.zip");
        CreateZip("sub/nested.zip");

        var discovery = new ArchiveDiscovery();
        var results = await discovery.DiscoverAsync(_tempRoot, maxDepth: 0); // Clamped to 1

        results.Should().ContainSingle(a => a.VirtualPath == "root.zip");
    }

    [Fact]
    public async Task DiscoverAsync_DepthClampedToMax()
    {
        CreateZip("root.zip");

        var discovery = new ArchiveDiscovery();
        var results = await discovery.DiscoverAsync(_tempRoot, maxDepth: 100); // Clamped to 6

        results.Should().ContainSingle(a => a.VirtualPath == "root.zip");
    }

    // === Path normalization ===

    [Fact]
    public async Task DiscoverAsync_VirtualPathUsesForwardSlashes()
    {
        CreateZip("games/fps/doom.zip");

        var discovery = new ArchiveDiscovery();
        var results = await discovery.DiscoverAsync(_tempRoot, maxDepth: 6);

        results.Should().ContainSingle();
        results[0].VirtualPath.Should().Be("games/fps/doom.zip");
        results[0].VirtualPath.Should().NotContain("\\");
    }

    // === Metadata ===

    [Fact]
    public async Task DiscoverAsync_DescriptorContainsMetadata()
    {
        CreateZip("test.zip");

        var discovery = new ArchiveDiscovery();
        var results = await discovery.DiscoverAsync(_tempRoot, maxDepth: 6);

        var descriptor = results.Should().ContainSingle().Which;
        descriptor.PhysicalPath.Should().EndWith("test.zip");
        descriptor.SizeBytes.Should().BeGreaterThan(0);
        descriptor.LastModifiedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        descriptor.Name.Should().Be("test.zip");
    }

    // === Empty and missing directories ===

    [Fact]
    public async Task DiscoverAsync_EmptyDirectory_ReturnsEmptyList()
    {
        var discovery = new ArchiveDiscovery();
        var results = await discovery.DiscoverAsync(_tempRoot, maxDepth: 6);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_NonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        var discovery = new ArchiveDiscovery();

        Func<Task> act = () => discovery.DiscoverAsync(Path.Combine(_tempRoot, "nonexistent"), maxDepth: 6);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    // === Cancellation ===

    [Fact]
    public async Task DiscoverAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        CreateZip("test.zip");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var discovery = new ArchiveDiscovery();

        Func<Task> act = () => discovery.DiscoverAsync(_tempRoot, maxDepth: 6, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // === Integration: multi-folder structure ===

    [Fact]
    public async Task DiscoverAsync_MultiFolderStructure_FindsAll()
    {
        CreateZip("games/doom.zip");
        CreateZip("games/quake.zip");
        CreateZip("docs/manual.zip");
        CreateZip("backup.zip");

        var discovery = new ArchiveDiscovery();
        var results = await discovery.DiscoverAsync(_tempRoot, maxDepth: 6);

        results.Should().HaveCount(4);
        results.Select(r => r.VirtualPath).Should().BeEquivalentTo(
            "games/doom.zip", "games/quake.zip", "docs/manual.zip", "backup.zip");
    }
}
