using Spectre.Console;

namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Displays visually prominent user-facing notices using Spectre.Console panels.
/// These are mixed inline with Serilog log output for startup messages.
/// </summary>
public static class UserNotice
{
    /// <summary>
    /// Displays a blue-bordered TIP panel.
    /// </summary>
    public static void Tip(string body)
    {
        var panel = new Panel(Markup.Escape(body))
            .Header("[blue bold] TIP [/]")
            .RoundedBorder()
            .BorderColor(Color.Blue)
            .Padding(1, 0);

        AnsiConsole.Write(panel);
    }

    /// <summary>
    /// Displays a yellow-bordered WARNING panel.
    /// </summary>
    public static void Warning(string body)
    {
        var panel = new Panel(Markup.Escape(body))
            .Header("[yellow bold] WARNING [/]")
            .RoundedBorder()
            .BorderColor(Color.Yellow)
            .Padding(1, 0);

        AnsiConsole.Write(panel);
    }

    /// <summary>
    /// Displays a red double-bordered ERROR panel.
    /// </summary>
    public static void Error(string body)
    {
        var panel = new Panel(Markup.Escape(body))
            .Header("[red bold] ERROR [/]")
            .DoubleBorder()
            .BorderColor(Color.Red)
            .Padding(1, 0);

        AnsiConsole.Write(panel);
    }
}
