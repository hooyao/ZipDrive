namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Identifies Windows shell metadata paths that never exist inside ZIP archives.
/// Short-circuiting these in the WinFsp adapter avoids eagerly parsing ZIP Central
/// Directories just to return FileNotFound for probes like <c>\archive.zip\desktop.ini</c>.
/// </summary>
public static class ShellMetadataFilter
{
    /// <summary>
    /// Windows Explorer metadata filenames that never exist inside ZIP archives.
    /// </summary>
    private static readonly string[] MetadataFileNames =
    [
        "desktop.ini",
        "autorun.inf",
        "thumbs.db",
        "folder.jpg",
        "folder.gif",
        "icon.ico",
    ];

    /// <summary>
    /// Top-level path segments probed by Windows shell that never exist inside archives.
    /// </summary>
    private static readonly string[] MetadataPathPrefixes =
    [
        "$RECYCLE.BIN",
        "System Volume Information",
    ];

    /// <summary>
    /// Returns true if <paramref name="path"/> targets a Windows shell metadata file
    /// or a well-known shell prefix that can never exist inside a ZIP archive.
    /// Zero-allocation: works directly on the span without converting to string.
    /// </summary>
    public static bool IsShellMetadataPath(ReadOnlySpan<char> path)
    {
        int lastSep = LastSeparatorIndex(path);
        ReadOnlySpan<char> lastSegment = lastSep >= 0 ? path[(lastSep + 1)..] : path;

        if (lastSegment.Length > 0 && SpanMatchesAny(lastSegment, MetadataFileNames))
            return true;

        ReadOnlySpan<char> trimmed = TrimLeadingSeparators(path);
        int sep = SeparatorIndex(trimmed);
        ReadOnlySpan<char> firstSegment = sep >= 0 ? trimmed[..sep] : trimmed;

        if (firstSegment.Length > 0 && SpanMatchesAny(firstSegment, MetadataPathPrefixes))
            return true;

        return false;
    }

    private static int LastSeparatorIndex(ReadOnlySpan<char> path)
    {
        int backslash = path.LastIndexOf('\\');
        int slash = path.LastIndexOf('/');
        return Math.Max(backslash, slash);
    }

    private static int SeparatorIndex(ReadOnlySpan<char> path)
    {
        int backslash = path.IndexOf('\\');
        int slash = path.IndexOf('/');

        if (backslash < 0)
            return slash;
        if (slash < 0)
            return backslash;
        return Math.Min(backslash, slash);
    }

    private static ReadOnlySpan<char> TrimLeadingSeparators(ReadOnlySpan<char> path)
    {
        int index = 0;
        while (index < path.Length && (path[index] == '\\' || path[index] == '/'))
            index++;
        return path[index..];
    }

    private static bool SpanMatchesAny(ReadOnlySpan<char> value, string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (value.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
