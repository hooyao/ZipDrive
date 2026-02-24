using FluentAssertions;
using ZipDrive.Domain.Exceptions;
using ZipDrive.TestHelpers;

namespace ZipDrive.IntegrationTests;

/// <summary>
/// 11.2 Virtual Folder Navigation integration tests.
/// </summary>
[Collection("VfsIntegration")]
public class VirtualFolderNavigationTests
{
    private readonly VfsTestFixture _fixture;

    public VirtualFolderNavigationTests(VfsTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ListRoot_ContainsFoldersAndArchives()
    {
        var entries = await _fixture.Vfs.ListDirectoryAsync("");

        entries.Should().Contain(e => e.Name == "games" && e.IsDirectory);
        entries.Should().Contain(e => e.Name == "docs" && e.IsDirectory);
    }

    [Fact]
    public async Task ListGames_ContainsSubfolders()
    {
        var entries = await _fixture.Vfs.ListDirectoryAsync("games");

        entries.Should().Contain(e => e.Name == "fps" && e.IsDirectory);
        entries.Should().Contain(e => e.Name == "rpg" && e.IsDirectory);
    }

    [Fact]
    public async Task ListGamesFps_ContainsArchives()
    {
        var entries = await _fixture.Vfs.ListDirectoryAsync("games/fps");

        entries.Should().OnlyContain(e => e.IsDirectory); // Archives appear as directories
        entries.Should().Contain(e => e.Name.EndsWith(".zip"));
    }

    [Fact]
    public async Task GetFileInfo_VirtualFolder_IsDirectory()
    {
        var info = await _fixture.Vfs.GetFileInfoAsync("games");

        info.IsDirectory.Should().BeTrue();
        info.Name.Should().Be("games");
    }

    [Fact]
    public async Task DirectoryExists_VirtualFolder_ReturnsTrue()
    {
        (await _fixture.Vfs.DirectoryExistsAsync("games")).Should().BeTrue();
    }

    [Fact]
    public async Task FileExists_VirtualFolder_ReturnsFalse()
    {
        (await _fixture.Vfs.FileExistsAsync("games")).Should().BeFalse();
    }
}
