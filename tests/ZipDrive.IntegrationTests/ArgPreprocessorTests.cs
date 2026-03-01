using System.Runtime.Versioning;
using FluentAssertions;
using ZipDrive.Cli;

namespace ZipDrive.IntegrationTests;

[SupportedOSPlatform("windows")]
public class ArgPreprocessorTests
{
    [Fact]
    public void BareArg_IsRewrittenToArchiveDirectory()
    {
        var result = ArgPreprocessor.RewriteBareArgs([@"D:\folder"]);

        result.Should().Equal(@"--Mount:ArchiveDirectory=D:\folder");
    }

    [Fact]
    public void NamedArg_IsPassedThroughUnchanged()
    {
        var args = new[] { @"--Mount:ArchiveDirectory=D:\folder" };

        var result = ArgPreprocessor.RewriteBareArgs(args);

        result.Should().Equal(args);
    }

    [Fact]
    public void EmptyArgs_ArePassedThroughUnchanged()
    {
        var result = ArgPreprocessor.RewriteBareArgs([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void BareArgWithExplicitOverride_ExplicitWins()
    {
        var result = ArgPreprocessor.RewriteBareArgs(
            [@"D:\folder", @"--Mount:ArchiveDirectory=E:\other"]);

        // Bare arg is prepended, explicit named arg comes after → last wins
        result.Should().Equal(
            @"--Mount:ArchiveDirectory=D:\folder",
            @"--Mount:ArchiveDirectory=E:\other");
    }
}
