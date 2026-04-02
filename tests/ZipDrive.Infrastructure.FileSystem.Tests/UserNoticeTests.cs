using FluentAssertions;

namespace ZipDrive.Infrastructure.FileSystem.Tests;

public class UserNoticeTests
{
    [Fact]
    public void Tip_DoesNotThrow()
    {
        var act = () => UserNotice.Tip("Test tip message");
        act.Should().NotThrow();
    }

    [Fact]
    public void Warning_DoesNotThrow()
    {
        var act = () => UserNotice.Warning("Test warning message");
        act.Should().NotThrow();
    }

    [Fact]
    public void Error_DoesNotThrow()
    {
        var act = () => UserNotice.Error("Test error message");
        act.Should().NotThrow();
    }

    [Fact]
    public void Tip_EscapesMarkupCharacters()
    {
        // Spectre.Console markup uses [brackets] — file paths with brackets should not throw
        var act = () => UserNotice.Tip("Path: C:\\[special]\\file.zip");
        act.Should().NotThrow();
    }

    [Fact]
    public void Error_EscapesMarkupCharacters()
    {
        var act = () => UserNotice.Error("File type \"[unknown]\" is not supported.");
        act.Should().NotThrow();
    }
}
