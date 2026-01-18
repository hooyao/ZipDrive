namespace ZipDriveV3.Domain.Models;

/// <summary>
/// Tree node for directory structure (used for FindFiles operations).
/// </summary>
/// <remarks>
/// <para>
/// This class represents a node in the hierarchical directory tree built
/// from ZIP archive entries. It is used by DokanNet's FindFiles operation
/// to enumerate directory contents efficiently.
/// </para>
/// <para>
/// The tree is built incrementally during Central Directory parsing,
/// with each entry being added to the appropriate position in the tree.
/// </para>
/// </remarks>
public sealed class DirectoryNode
{
    /// <summary>
    /// Name of this directory (not the full path).
    /// </summary>
    /// <remarks>
    /// For the root directory, this is an empty string.
    /// </remarks>
    public string Name { get; init; } = "";

    /// <summary>
    /// Full path from the archive root to this directory.
    /// </summary>
    /// <remarks>
    /// Uses forward slashes as separators, no leading or trailing slash.
    /// For the root directory, this is an empty string.
    /// Example: "folder/subfolder"
    /// </remarks>
    public string FullPath { get; init; } = "";

    /// <summary>
    /// Child directories keyed by name (case-insensitive).
    /// </summary>
    public Dictionary<string, DirectoryNode> Subdirectories { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Files in this directory keyed by name (case-insensitive).
    /// </summary>
    public Dictionary<string, ZipEntryInfo> Files { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Total number of direct children (subdirectories + files).
    /// </summary>
    public int ChildCount => Subdirectories.Count + Files.Count;

    /// <summary>
    /// True if this directory has no children.
    /// </summary>
    public bool IsEmpty => ChildCount == 0;

    /// <summary>
    /// Gets a child directory by name.
    /// </summary>
    /// <param name="name">Directory name (not a path).</param>
    /// <returns>The child directory, or null if not found.</returns>
    public DirectoryNode? GetSubdirectory(string name)
    {
        return Subdirectories.TryGetValue(name, out DirectoryNode? subdir) ? subdir : null;
    }

    /// <summary>
    /// Gets a file entry by name.
    /// </summary>
    /// <param name="name">File name (not a path).</param>
    /// <returns>The file entry info, or null if not found.</returns>
    public ZipEntryInfo? GetFile(string name)
    {
        return Files.TryGetValue(name, out ZipEntryInfo file) ? file : null;
    }

    /// <summary>
    /// Gets or creates a child directory.
    /// </summary>
    /// <param name="name">Directory name.</param>
    /// <returns>The existing or newly created child directory.</returns>
    public DirectoryNode GetOrAddSubdirectory(string name)
    {
        if (!Subdirectories.TryGetValue(name, out DirectoryNode? subdir))
        {
            string childPath = string.IsNullOrEmpty(FullPath) ? name : $"{FullPath}/{name}";
            subdir = new DirectoryNode { Name = name, FullPath = childPath };
            Subdirectories[name] = subdir;
        }
        return subdir;
    }

    /// <summary>
    /// Adds a file to this directory.
    /// </summary>
    /// <param name="name">File name.</param>
    /// <param name="entry">File entry info.</param>
    public void AddFile(string name, ZipEntryInfo entry)
    {
        Files[name] = entry;
    }
}
