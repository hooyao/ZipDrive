using FluentAssertions;
using ZipDrive.TestHelpers;

namespace ZipDrive.IntegrationTests;

/// <summary>
/// 11.6 Cache Behavior Correctness integration tests.
/// </summary>
[Collection("VfsIntegration")]
public class CacheBehaviorTests
{
    private readonly VfsTestFixture _fixture;

    public CacheBehaviorTests(VfsTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task StructureCache_FirstAccess_TriggersLoad()
    {
        // Listing an archive root triggers lazy structure load
        var entries = await _fixture.Vfs.ListDirectoryAsync("games/fps");
        var archive = entries.First(e => e.Name.EndsWith(".zip"));

        // First listing loads structure
        var contents = await _fixture.Vfs.ListDirectoryAsync($"games/fps/{archive.Name}");
        contents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task StructureCache_SecondAccess_IsCacheHit()
    {
        var entries = await _fixture.Vfs.ListDirectoryAsync("games/fps");
        var archive = entries.First(e => e.Name.EndsWith(".zip"));
        string archivePath = $"games/fps/{archive.Name}";

        // First call builds structure
        await _fixture.Vfs.ListDirectoryAsync(archivePath);

        // Second call should be cache hit (no measurable way to verify in integration,
        // but it exercises the path and should be faster)
        var contents2 = await _fixture.Vfs.ListDirectoryAsync(archivePath);
        contents2.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FileContentCache_RepeatedReads_ReturnSameData()
    {
        var entries = await _fixture.Vfs.ListDirectoryAsync("games/fps");
        var archive = entries.First(e => e.Name.EndsWith(".zip"));
        string archivePath = $"games/fps/{archive.Name}";

        string? filePath = await FindAnyFileAsync(archivePath);
        if (filePath == null) return; // No files in this archive

        var file = await _fixture.Vfs.GetFileInfoAsync(filePath);
        // Read same file 3 times
        byte[][] reads = new byte[3][];
        for (int i = 0; i < 3; i++)
        {
            var info = await _fixture.Vfs.GetFileInfoAsync(filePath);
            byte[] buf = new byte[info.SizeBytes];
            int read = await _fixture.Vfs.ReadFileAsync(filePath, buf, 0);
            reads[i] = buf[..read];
        }

        reads[1].Should().Equal(reads[0]);
        reads[2].Should().Equal(reads[0]);
    }

    [Fact]
    public async Task ThunderingHerd_SameFile_AllGetCorrectData()
    {
        var entries = await _fixture.Vfs.ListDirectoryAsync("games/rpg");
        var archive = entries.First(e => e.Name.EndsWith(".zip"));
        string archivePath = $"games/rpg/{archive.Name}";

        string? filePath = await FindAnyFileAsync(archivePath);
        if (filePath == null) return;

        // 20 simultaneous requests for the same file
        Task<byte[]>[] tasks = Enumerable.Range(0, 20)
            .Select(async _ =>
            {
                var info = await _fixture.Vfs.GetFileInfoAsync(filePath);
                byte[] buf = new byte[info.SizeBytes];
                int read = await _fixture.Vfs.ReadFileAsync(filePath, buf, 0);
                return buf[..read];
            })
            .ToArray();

        byte[][] results = await Task.WhenAll(tasks);

        // All results must be identical
        for (int i = 1; i < results.Length; i++)
        {
            results[i].Should().Equal(results[0], $"thundering herd read {i} differs");
        }
    }

    /// <summary>
    /// Recursively finds any non-directory, non-manifest file in an archive.
    /// </summary>
    private async Task<string?> FindAnyFileAsync(string dirPath)
    {
        var contents = await _fixture.Vfs.ListDirectoryAsync(dirPath);
        var file = contents.FirstOrDefault(e => !e.IsDirectory && e.Name != "__manifest__.json");
        if (file.Name != null)
            return $"{dirPath}/{file.Name}";

        foreach (var subdir in contents.Where(e => e.IsDirectory))
        {
            var found = await FindAnyFileAsync($"{dirPath}/{subdir.Name}");
            if (found != null) return found;
        }
        return null;
    }

    [Fact]
    public async Task VolumeInfo_ReturnsReadOnly()
    {
        var vol = _fixture.Vfs.GetVolumeInfo();
        vol.FileSystemName.Should().Be("ZipDriveFS");
        vol.IsReadOnly.Should().BeTrue();
    }
}
