using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using ZipDrive.Domain.Exceptions;
using ZipDrive.TestHelpers;

namespace ZipDrive.IntegrationTests;

/// <summary>
/// 11.4 File Read Correctness (Content Verification) integration tests.
/// Verifies that data read via VFS matches the SHA-256 in the embedded manifest.
/// </summary>
[Collection("VfsIntegration")]
public class FileReadCorrectnessTests
{
    private readonly VfsTestFixture _fixture;

    public FileReadCorrectnessTests(VfsTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ReadSmallFile_ContentMatchesManifest()
    {
        // Find an archive, read its manifest, verify a file
        string archivePath = await FindFirstArchivePathAsync("games/fps");
        ZipManifest manifest = await ReadManifestAsync(archivePath);

        ManifestEntry? fileEntry = manifest.Entries.FirstOrDefault(
            e => !e.IsDirectory && e.FileName != "__manifest__.json" && e.UncompressedSize > 0 && e.UncompressedSize < 50 * 1024 * 1024);

        if (fileEntry == null) return; // No suitable files in this profile

        byte[] content = await ReadEntireFileAsync($"{archivePath}/{fileEntry.FileName}");
        string actualSha256 = TestZipGenerator.ComputeSha256(content);

        actualSha256.Should().Be(fileEntry.Sha256, $"SHA-256 mismatch for {fileEntry.FileName}");
    }

    [Fact]
    public async Task ReadFile_WithOffset_ReturnsCorrectSlice()
    {
        string archivePath = await FindFirstArchivePathAsync("games/fps");
        ZipManifest manifest = await ReadManifestAsync(archivePath);

        ManifestEntry? fileEntry = manifest.Entries.FirstOrDefault(
            e => !e.IsDirectory && e.FileName != "__manifest__.json" && e.UncompressedSize > 100);

        if (fileEntry == null) return;

        // Read full file
        byte[] full = await ReadEntireFileAsync($"{archivePath}/{fileEntry.FileName}");

        // Read with offset
        long offset = Math.Min(10, fileEntry.UncompressedSize - 1);
        byte[] buffer = new byte[50];
        int bytesRead = await _fixture.Vfs.ReadFileAsync(
            $"{archivePath}/{fileEntry.FileName}", buffer, offset);

        bytesRead.Should().BeGreaterThan(0);
        buffer.AsSpan(0, bytesRead).SequenceEqual(full.AsSpan((int)offset, bytesRead)).Should().BeTrue();
    }

    [Fact]
    public async Task ReadFile_AtEof_ReturnsZero()
    {
        string archivePath = await FindFirstArchivePathAsync("games/fps");
        ZipManifest manifest = await ReadManifestAsync(archivePath);

        ManifestEntry? fileEntry = manifest.Entries.FirstOrDefault(
            e => !e.IsDirectory && e.FileName != "__manifest__.json" && e.UncompressedSize > 0);

        if (fileEntry == null) return;

        byte[] buffer = new byte[100];
        int bytesRead = await _fixture.Vfs.ReadFileAsync(
            $"{archivePath}/{fileEntry.FileName}", buffer, fileEntry.UncompressedSize);

        bytesRead.Should().Be(0);
    }

    [Fact]
    public async Task ReadFile_BeyondEof_ReturnsZero()
    {
        string archivePath = await FindFirstArchivePathAsync("games/fps");
        ZipManifest manifest = await ReadManifestAsync(archivePath);

        ManifestEntry? fileEntry = manifest.Entries.FirstOrDefault(
            e => !e.IsDirectory && e.FileName != "__manifest__.json" && e.UncompressedSize > 0);

        if (fileEntry == null) return;

        byte[] buffer = new byte[100];
        int bytesRead = await _fixture.Vfs.ReadFileAsync(
            $"{archivePath}/{fileEntry.FileName}", buffer, fileEntry.UncompressedSize + 1000);

        bytesRead.Should().Be(0);
    }

    [Fact]
    public async Task ReadEmptyFile_ReturnsZero()
    {
        // Edge cases profile has empty files
        string? archivePath = await FindArchiveInFolderAsync("edge");
        if (archivePath == null) return;

        ZipManifest manifest = await ReadManifestAsync(archivePath);
        ManifestEntry? emptyFile = manifest.Entries.FirstOrDefault(
            e => !e.IsDirectory && e.UncompressedSize == 0);

        if (emptyFile == null) return;

        byte[] buffer = new byte[100];
        int bytesRead = await _fixture.Vfs.ReadFileAsync(
            $"{archivePath}/{emptyFile.FileName}", buffer, 0);

        bytesRead.Should().Be(0);
    }

    [Fact]
    public async Task SequentialChunkedRead_MatchesSha256()
    {
        string archivePath = await FindFirstArchivePathAsync("games/fps");
        ZipManifest manifest = await ReadManifestAsync(archivePath);

        ManifestEntry? fileEntry = manifest.Entries.FirstOrDefault(
            e => !e.IsDirectory && e.FileName != "__manifest__.json" && e.UncompressedSize > 100);

        if (fileEntry == null) return;

        // Read in 4KB chunks
        using (MemoryStream ms = new())
        {
            byte[] chunk = new byte[4096];
            long offset = 0;
            while (true)
            {
                int read = await _fixture.Vfs.ReadFileAsync(
                    $"{archivePath}/{fileEntry.FileName}", chunk, offset);
                if (read == 0) break;
                ms.Write(chunk, 0, read);
                offset += read;
            }

            string actualSha256 = TestZipGenerator.ComputeSha256(ms.ToArray());
            actualSha256.Should().Be(fileEntry.Sha256);
        }
    }

    [Fact]
    public async Task CacheHit_ReturnsSameContent()
    {
        string archivePath = await FindFirstArchivePathAsync("games/fps");
        ZipManifest manifest = await ReadManifestAsync(archivePath);

        ManifestEntry? fileEntry = manifest.Entries.FirstOrDefault(
            e => !e.IsDirectory && e.FileName != "__manifest__.json" && e.UncompressedSize > 0);

        if (fileEntry == null) return;

        string fullPath = $"{archivePath}/{fileEntry.FileName}";

        byte[] read1 = await ReadEntireFileAsync(fullPath);
        byte[] read2 = await ReadEntireFileAsync(fullPath);

        read1.Should().Equal(read2);
    }

    [Fact]
    public async Task ReadFile_NonExistent_ThrowsVfsFileNotFound()
    {
        string archivePath = await FindFirstArchivePathAsync("games/fps");

        Func<Task> act = () => _fixture.Vfs.ReadFileAsync(
            $"{archivePath}/DOES_NOT_EXIST.bin", new byte[100], 0);

        await act.Should().ThrowAsync<VfsFileNotFoundException>();
    }

    [Fact]
    public async Task ReadFile_Directory_ThrowsVfsAccessDenied()
    {
        string archivePath = await FindFirstArchivePathAsync("games/fps");
        var contents = await _fixture.Vfs.ListDirectoryAsync(archivePath);
        var dir = contents.FirstOrDefault(e => e.IsDirectory);

        if (dir.Name == null) return;

        Func<Task> act = () => _fixture.Vfs.ReadFileAsync(
            $"{archivePath}/{dir.Name}", new byte[100], 0);

        await act.Should().ThrowAsync<VfsAccessDeniedException>();
    }

    [Fact]
    public async Task VerifyAllFilesInArchive_AllMatchManifest()
    {
        // Pick one archive and verify every file
        string archivePath = await FindFirstArchivePathAsync("games/fps");
        ZipManifest manifest = await ReadManifestAsync(archivePath);

        foreach (ManifestEntry entry in manifest.Entries.Where(
            e => !e.IsDirectory && e.FileName != "__manifest__.json" && e.UncompressedSize > 0))
        {
            byte[] content = await ReadEntireFileAsync($"{archivePath}/{entry.FileName}");
            string sha256 = TestZipGenerator.ComputeSha256(content);
            sha256.Should().Be(entry.Sha256, $"Mismatch in {entry.FileName}");
        }
    }

    // === Helpers ===

    private async Task<string> FindFirstArchivePathAsync(string folderPath)
    {
        var entries = await _fixture.Vfs.ListDirectoryAsync(folderPath);
        var archive = entries.First(e => e.Name.EndsWith(".zip"));
        return $"{folderPath}/{archive.Name}";
    }

    private async Task<string?> FindArchiveInFolderAsync(string folderPath)
    {
        try
        {
            var entries = await _fixture.Vfs.ListDirectoryAsync(folderPath);
            var archive = entries.FirstOrDefault(e => e.Name.EndsWith(".zip"));
            return archive.Name != null ? $"{folderPath}/{archive.Name}" : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ZipManifest> ReadManifestAsync(string archivePath)
    {
        byte[] buffer = new byte[1024 * 1024]; // 1MB should be enough for manifest
        int bytesRead = await _fixture.Vfs.ReadFileAsync($"{archivePath}/__manifest__.json", buffer, 0);
        // Skip UTF-8 BOM if present
        int offset = 0;
        if (bytesRead >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            offset = 3;
        string json = System.Text.Encoding.UTF8.GetString(buffer, offset, bytesRead - offset);
        return JsonSerializer.Deserialize<ZipManifest>(json)!;
    }

    private async Task<byte[]> ReadEntireFileAsync(string path)
    {
        var info = await _fixture.Vfs.GetFileInfoAsync(path);
        byte[] buffer = new byte[info.SizeBytes];
        int read = await _fixture.Vfs.ReadFileAsync(path, buffer, 0);
        return buffer[..read];
    }
}
