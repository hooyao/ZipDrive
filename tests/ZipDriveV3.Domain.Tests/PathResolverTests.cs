using FluentAssertions;
using ZipDriveV3.Application.Services;
using ZipDriveV3.Domain.Models;

namespace ZipDriveV3.Domain.Tests;

/// <summary>
/// Tests for path normalization logic in PathResolver.
/// Full path resolution tests (with archive trie) are in task section 7.
/// </summary>
public class PathResolverTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("\\", "")]
    [InlineData("/", "")]
    public void Normalize_RootPaths_ReturnsEmpty(string? input, string expected)
    {
        PathResolver.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("\\games\\doom.zip\\maps\\e1m1.wad", "games/doom.zip/maps/e1m1.wad")]
    [InlineData("/games/doom.zip/maps/e1m1.wad", "games/doom.zip/maps/e1m1.wad")]
    [InlineData("games/doom.zip/maps/e1m1.wad", "games/doom.zip/maps/e1m1.wad")]
    public void Normalize_BackslashConversion(string input, string expected)
    {
        PathResolver.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("/games/doom.zip/", "games/doom.zip")]
    [InlineData("\\games\\doom.zip\\", "games/doom.zip")]
    public void Normalize_TrimsLeadingAndTrailingSlashes(string input, string expected)
    {
        PathResolver.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("games//doom.zip///maps/e1m1.wad", "games/doom.zip/maps/e1m1.wad")]
    public void Normalize_CollapsesConsecutiveSlashes(string input, string expected)
    {
        PathResolver.Normalize(input).Should().Be(expected);
    }
}
