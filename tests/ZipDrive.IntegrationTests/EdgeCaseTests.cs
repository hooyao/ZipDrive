using FluentAssertions;
using ZipDrive.Domain.Exceptions;
using ZipDrive.TestHelpers;

namespace ZipDrive.IntegrationTests;

/// <summary>
/// 11.5 Edge Cases integration tests.
/// </summary>
[Collection("VfsIntegration")]
public class EdgeCaseTests
{
    private readonly VfsTestFixture _fixture;

    public EdgeCaseTests(VfsTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PathWithBackslashes_NormalizesCorrectly()
    {
        // Access using backslashes - should normalize
        var entries = await _fixture.Vfs.ListDirectoryAsync("games\\fps");
        entries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PathWithDoubleSlashes_NormalizesCorrectly()
    {
        var entries = await _fixture.Vfs.ListDirectoryAsync("games//fps");
        entries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PathWithTrailingSlash_Works()
    {
        var entries = await _fixture.Vfs.ListDirectoryAsync("games/fps/");
        entries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CaseInsensitive_ArchivePath_Resolves()
    {
        // Windows case-insensitive: GAMES/FPS should work
        var entries = await _fixture.Vfs.ListDirectoryAsync("GAMES/FPS");
        entries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadFile_NonExistent_ThrowsVfsFileNotFound()
    {
        Func<Task> act = () => _fixture.Vfs.ReadFileAsync("nonexistent/path.txt", new byte[100], 0);
        await act.Should().ThrowAsync<VfsFileNotFoundException>();
    }

    [Fact]
    public async Task ListDirectory_NonExistent_ThrowsVfsDirectoryNotFound()
    {
        Func<Task> act = () => _fixture.Vfs.ListDirectoryAsync("nonexistent/dir");
        await act.Should().ThrowAsync<VfsDirectoryNotFoundException>();
    }

    [Fact]
    public async Task ReadFile_VirtualRoot_ThrowsVfsAccessDenied()
    {
        Func<Task> act = () => _fixture.Vfs.ReadFileAsync("", new byte[100], 0);
        await act.Should().ThrowAsync<VfsAccessDeniedException>();
    }

    [Fact]
    public async Task ReadFile_VirtualFolder_ThrowsVfsAccessDenied()
    {
        Func<Task> act = () => _fixture.Vfs.ReadFileAsync("games", new byte[100], 0);
        await act.Should().ThrowAsync<VfsAccessDeniedException>();
    }

    [Fact]
    public async Task ConcurrentReads_SameFile_AllCorrect()
    {
        string archivePath = await FindFirstArchivePathAsync("games/fps");
        var contents = await _fixture.Vfs.ListDirectoryAsync(archivePath);
        var file = contents.FirstOrDefault(e => !e.IsDirectory && e.Name != "__manifest__.json");
        if (file.Name == null) return;

        string filePath = $"{archivePath}/{file.Name}";

        // 10 concurrent reads
        Task<byte[]>[] tasks = Enumerable.Range(0, 10)
            .Select(async _ =>
            {
                var info = await _fixture.Vfs.GetFileInfoAsync(filePath);
                byte[] buf = new byte[info.SizeBytes];
                int read = await _fixture.Vfs.ReadFileAsync(filePath, buf, 0);
                return buf[..read];
            })
            .ToArray();

        byte[][] results = await Task.WhenAll(tasks);

        // All should be identical
        for (int i = 1; i < results.Length; i++)
        {
            results[i].Should().Equal(results[0], $"concurrent read {i} differs from first read");
        }
    }

    private async Task<string> FindFirstArchivePathAsync(string folderPath)
    {
        var entries = await _fixture.Vfs.ListDirectoryAsync(folderPath);
        var archive = entries.First(e => e.Name.EndsWith(".zip"));
        return $"{folderPath}/{archive.Name}";
    }
}
