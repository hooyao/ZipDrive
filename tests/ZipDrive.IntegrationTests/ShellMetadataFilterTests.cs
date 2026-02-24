using FluentAssertions;
using ZipDrive.Infrastructure.FileSystem;

namespace ZipDrive.IntegrationTests;

public class ShellMetadataFilterTests
{
    // === Shell metadata filenames ===

    [Theory]
    [InlineData(@"\archive.zip\desktop.ini")]
    [InlineData(@"\archive.zip\Desktop.INI")]
    [InlineData(@"\archive.zip\DESKTOP.INI")]
    [InlineData(@"\2000\archive.zip\desktop.ini")]
    [InlineData(@"\archive.zip\autorun.inf")]
    [InlineData(@"\archive.zip\AutoRun.Inf")]
    [InlineData(@"\archive.zip\thumbs.db")]
    [InlineData(@"\archive.zip\Thumbs.db")]
    [InlineData(@"\archive.zip\folder.jpg")]
    [InlineData(@"\archive.zip\folder.gif")]
    [InlineData(@"\archive.zip\icon.ico")]
    [InlineData(@"\archive.zip\subfolder\desktop.ini")]
    [InlineData(@"\archive.zip\a\b\c\thumbs.db")]
    public void IsShellMetadataPath_ReturnsTrue_ForKnownMetadataFiles(string path)
    {
        ShellMetadataFilter.IsShellMetadataPath(path).Should().BeTrue();
    }

    // === Shell metadata prefixes ===

    [Theory]
    [InlineData(@"\$RECYCLE.BIN")]
    [InlineData(@"\$RECYCLE.BIN\something")]
    [InlineData(@"\$Recycle.Bin\file.txt")]
    [InlineData(@"\System Volume Information")]
    [InlineData(@"\System Volume Information\tracking.log")]
    public void IsShellMetadataPath_ReturnsTrue_ForKnownPrefixes(string path)
    {
        ShellMetadataFilter.IsShellMetadataPath(path).Should().BeTrue();
    }

    // === Paths that should NOT be short-circuited ===

    [Theory]
    [InlineData(@"\")]
    [InlineData(@"\2000")]
    [InlineData(@"\archive.zip")]
    [InlineData(@"\archive.zip\readme.txt")]
    [InlineData(@"\archive.zip\subfolder\image.jpg")]
    [InlineData(@"\2000\archive.zip\photo.jpg")]
    [InlineData(@"\archive.zip\folder")]
    [InlineData(@"\archive.zip\$RECYCLE.BIN\file.txt")]
    [InlineData(@"\folder\System Volume Information\file.txt")]
    public void IsShellMetadataPath_ReturnsFalse_ForNormalPaths(string path)
    {
        ShellMetadataFilter.IsShellMetadataPath(path).Should().BeFalse();
    }

    // === MountOptions default ===

    [Fact]
    public void MountOptions_ShortCircuitShellMetadata_DefaultsToTrue()
    {
        var opts = new MountOptions();
        opts.ShortCircuitShellMetadata.Should().BeTrue();
    }
}
