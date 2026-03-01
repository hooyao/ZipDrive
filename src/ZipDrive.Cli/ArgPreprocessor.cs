namespace ZipDrive.Cli;

/// <summary>
/// Rewrites bare positional arguments (e.g., from drag-and-drop) into named
/// configuration keys that the .NET Generic Host config pipeline understands.
/// </summary>
public static class ArgPreprocessor
{
    /// <summary>
    /// If <paramref name="args"/>[0] is a bare path (not prefixed with "--"),
    /// prepends <c>--Mount:ArchiveDirectory=&lt;path&gt;</c> so it feeds into
    /// configuration binding. Prepending ensures an explicit named arg later
    /// in the array wins via last-wins semantics.
    /// </summary>
    public static string[] RewriteBareArgs(string[] args)
    {
        if (args.Length > 0 && !args[0].StartsWith("--"))
        {
            return [$"--Mount:ArchiveDirectory={args[0]}", .. args[1..]];
        }

        return args;
    }
}
