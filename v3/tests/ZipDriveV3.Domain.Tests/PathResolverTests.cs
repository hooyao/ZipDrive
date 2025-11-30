using FluentAssertions;
using ZipDriveV3.Application.Services;
using ZipDriveV3.Domain.Models;

namespace ZipDriveV3.Domain.Tests;

public class PathResolverTests
{
    private readonly PathResolver _resolver = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\\")]
    public void Resolve_RootPaths_ReturnsRootDirectory(string? path)
    {
        // Act
        var result = _resolver.Resolve(path!);

        // Assert
        result.Status.Should().Be(PathResolutionStatus.RootDirectory);
        result.ArchiveKey.Should().BeNull();
        result.InternalPath.Should().Be("");
    }

    [Fact]
    public void Resolve_ArchiveRoot_ReturnsArchiveRootStatus()
    {
        // Arrange
        const string path = "\\archive.zip";

        // Act
        var result = _resolver.Resolve(path);

        // Assert
        result.Status.Should().Be(PathResolutionStatus.ArchiveRoot);
        result.ArchiveKey.Should().Be("archive.zip");
        result.InternalPath.Should().Be("");
    }

    [Fact]
    public void Resolve_ArchiveRootWithTrailingSlash_ReturnsArchiveRoot()
    {
        // Arrange
        const string path = "\\archive.zip\\";

        // Act
        var result = _resolver.Resolve(path);

        // Assert
        result.Status.Should().Be(PathResolutionStatus.ArchiveRoot);
        result.ArchiveKey.Should().Be("archive.zip");
        result.InternalPath.Should().Be("");
    }

    [Fact]
    public void Resolve_FileInArchiveRoot_ReturnsSuccess()
    {
        // Arrange
        const string path = "\\archive.zip\\file.txt";

        // Act
        var result = _resolver.Resolve(path);

        // Assert
        result.Status.Should().Be(PathResolutionStatus.Success);
        result.ArchiveKey.Should().Be("archive.zip");
        result.InternalPath.Should().Be("file.txt");
    }

    [Fact]
    public void Resolve_NestedPath_ReturnsSuccessWithForwardSlashes()
    {
        // Arrange
        const string path = "\\archive.zip\\folder\\subfolder\\file.txt";

        // Act
        var result = _resolver.Resolve(path);

        // Assert
        result.Status.Should().Be(PathResolutionStatus.Success);
        result.ArchiveKey.Should().Be("archive.zip");
        result.InternalPath.Should().Be("folder/subfolder/file.txt");
    }

    [Fact]
    public void Resolve_DeepNesting_ConvertsBackslashesToForwardSlashes()
    {
        // Arrange
        const string path = "\\data.zip\\level1\\level2\\level3\\level4\\file.dat";

        // Act
        var result = _resolver.Resolve(path);

        // Assert
        result.Status.Should().Be(PathResolutionStatus.Success);
        result.ArchiveKey.Should().Be("data.zip");
        result.InternalPath.Should().Be("level1/level2/level3/level4/file.dat");
    }

    [Fact]
    public void Resolve_ArchiveNameWithSpaces_HandlesCorrectly()
    {
        // Arrange
        const string path = "\\my archive.zip\\folder\\file.txt";

        // Act
        var result = _resolver.Resolve(path);

        // Assert
        result.Status.Should().Be(PathResolutionStatus.Success);
        result.ArchiveKey.Should().Be("my archive.zip");
        result.InternalPath.Should().Be("folder/file.txt");
    }

    [Fact]
    public void Resolve_MultiplePaths_EachProcessedIndependently()
    {
        // Arrange
        var paths = new[]
        {
            ("\\", PathResolutionStatus.RootDirectory, (string?)null, ""),
            ("\\archive1.zip", PathResolutionStatus.ArchiveRoot, "archive1.zip", ""),
            ("\\archive2.zip\\file.txt", PathResolutionStatus.Success, "archive2.zip", "file.txt"),
            ("\\archive3.zip\\dir\\file.dat", PathResolutionStatus.Success, "archive3.zip", "dir/file.dat")
        };

        foreach (var (path, expectedStatus, expectedKey, expectedInternal) in paths)
        {
            // Act
            var result = _resolver.Resolve(path);

            // Assert
            result.Status.Should().Be(expectedStatus, $"for path: {path}");
            result.ArchiveKey.Should().Be(expectedKey, $"for path: {path}");
            result.InternalPath.Should().Be(expectedInternal, $"for path: {path}");
        }
    }
}
