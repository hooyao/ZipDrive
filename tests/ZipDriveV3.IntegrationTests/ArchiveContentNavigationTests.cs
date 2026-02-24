using FluentAssertions;
using ZipDriveV3.Domain.Exceptions;
using ZipDriveV3.TestHelpers;

namespace ZipDriveV3.IntegrationTests;

/// <summary>
/// 11.3 Archive Content Navigation integration tests.
/// </summary>
[Collection("VfsIntegration")]
public class ArchiveContentNavigationTests
{
    private readonly VfsTestFixture _fixture;

    public ArchiveContentNavigationTests(VfsTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ListArchiveRoot_ContainsEntries()
    {
        // Find first archive in games/fps
        var fpsEntries = await _fixture.Vfs.ListDirectoryAsync("games/fps");
        var firstArchive = fpsEntries.First(e => e.Name.EndsWith(".zip"));

        var archiveContents = await _fixture.Vfs.ListDirectoryAsync($"games/fps/{firstArchive.Name}");
        archiveContents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ListArchiveSubdir_ContainsFiles()
    {
        // Find an archive, list root, find a subdirectory, list it
        var fpsEntries = await _fixture.Vfs.ListDirectoryAsync("games/fps");
        var firstArchive = fpsEntries.First(e => e.Name.EndsWith(".zip"));
        string archivePath = $"games/fps/{firstArchive.Name}";

        var rootContents = await _fixture.Vfs.ListDirectoryAsync(archivePath);
        var subdir = rootContents.FirstOrDefault(e => e.IsDirectory);

        if (subdir.Name != null)
        {
            var subdirContents = await _fixture.Vfs.ListDirectoryAsync($"{archivePath}/{subdir.Name}");
            subdirContents.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task GetFileInfo_FileInsideArchive_HasSize()
    {
        // Navigate to a file inside an archive
        var fpsEntries = await _fixture.Vfs.ListDirectoryAsync("games/fps");
        var firstArchive = fpsEntries.First(e => e.Name.EndsWith(".zip"));
        string archivePath = $"games/fps/{firstArchive.Name}";

        var rootContents = await _fixture.Vfs.ListDirectoryAsync(archivePath);
        var file = rootContents.FirstOrDefault(e => !e.IsDirectory && e.Name != "__manifest__.json");

        if (file.Name != null)
        {
            var info = await _fixture.Vfs.GetFileInfoAsync($"{archivePath}/{file.Name}");
            info.IsDirectory.Should().BeFalse();
            info.SizeBytes.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task FileExists_InsideArchive_ReturnsTrue()
    {
        var fpsEntries = await _fixture.Vfs.ListDirectoryAsync("games/fps");
        var firstArchive = fpsEntries.First(e => e.Name.EndsWith(".zip"));
        string archivePath = $"games/fps/{firstArchive.Name}";

        // __manifest__.json always exists
        (await _fixture.Vfs.FileExistsAsync($"{archivePath}/__manifest__.json")).Should().BeTrue();
    }

    [Fact]
    public async Task DirectoryExists_InsideArchive_ReturnsTrue()
    {
        var fpsEntries = await _fixture.Vfs.ListDirectoryAsync("games/fps");
        var firstArchive = fpsEntries.First(e => e.Name.EndsWith(".zip"));
        string archivePath = $"games/fps/{firstArchive.Name}";

        var rootContents = await _fixture.Vfs.ListDirectoryAsync(archivePath);
        var subdir = rootContents.FirstOrDefault(e => e.IsDirectory);

        if (subdir.Name != null)
        {
            (await _fixture.Vfs.DirectoryExistsAsync($"{archivePath}/{subdir.Name}")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task FileExists_NonExistent_ReturnsFalse()
    {
        var fpsEntries = await _fixture.Vfs.ListDirectoryAsync("games/fps");
        var firstArchive = fpsEntries.First(e => e.Name.EndsWith(".zip"));

        (await _fixture.Vfs.FileExistsAsync($"games/fps/{firstArchive.Name}/DOES_NOT_EXIST.xyz")).Should().BeFalse();
    }

    [Fact]
    public async Task FileExists_Directory_ReturnsFalse()
    {
        var fpsEntries = await _fixture.Vfs.ListDirectoryAsync("games/fps");
        var firstArchive = fpsEntries.First(e => e.Name.EndsWith(".zip"));
        string archivePath = $"games/fps/{firstArchive.Name}";

        var rootContents = await _fixture.Vfs.ListDirectoryAsync(archivePath);
        var subdir = rootContents.FirstOrDefault(e => e.IsDirectory);

        if (subdir.Name != null)
        {
            (await _fixture.Vfs.FileExistsAsync($"{archivePath}/{subdir.Name}")).Should().BeFalse();
        }
    }
}
