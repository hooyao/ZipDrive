namespace ZipDrive.Domain.Models;

/// <summary>
/// Result of resolving a path against the archive trie.
/// </summary>
public readonly record struct ArchiveTrieResult
{
    /// <summary>
    /// The type of path that was resolved.
    /// </summary>
    public required ArchiveTrieStatus Status { get; init; }

    /// <summary>
    /// The matched archive descriptor. Set when Status is ArchiveRoot or InsideArchive.
    /// </summary>
    public ArchiveDescriptor? Archive { get; init; }

    /// <summary>
    /// Path inside the archive (e.g., "maps/e1m1.wad").
    /// Empty string for archive root. Only meaningful when Status is ArchiveRoot or InsideArchive.
    /// </summary>
    public string InternalPath { get; init; }

    /// <summary>
    /// Virtual folder path (e.g., "games"). Set when Status is VirtualFolder.
    /// </summary>
    public string? VirtualFolderPath { get; init; }

    /// <summary>Creates a result for the virtual root path.</summary>
    public static ArchiveTrieResult VirtualRoot() =>
        new() { Status = ArchiveTrieStatus.VirtualRoot, InternalPath = "" };

    /// <summary>Creates a result for a virtual folder path.</summary>
    public static ArchiveTrieResult Folder(string path) =>
        new() { Status = ArchiveTrieStatus.VirtualFolder, VirtualFolderPath = path, InternalPath = "" };

    /// <summary>Creates a result for a path that matches an archive root exactly.</summary>
    public static ArchiveTrieResult AtArchiveRoot(ArchiveDescriptor archive) =>
        new() { Status = ArchiveTrieStatus.ArchiveRoot, Archive = archive, InternalPath = "" };

    /// <summary>Creates a result for a path inside an archive.</summary>
    public static ArchiveTrieResult Inside(ArchiveDescriptor archive, string internalPath) =>
        new() { Status = ArchiveTrieStatus.InsideArchive, Archive = archive, InternalPath = internalPath };

    /// <summary>Creates a result for a path that does not exist.</summary>
    public static ArchiveTrieResult NotFound() =>
        new() { Status = ArchiveTrieStatus.NotFound, InternalPath = "" };
}

/// <summary>
/// The type of virtual path resolved by the archive trie.
/// </summary>
public enum ArchiveTrieStatus
{
    /// <summary>Empty path - the virtual root.</summary>
    VirtualRoot,

    /// <summary>Path is a virtual folder containing archives or subfolders.</summary>
    VirtualFolder,

    /// <summary>Path matches an archive exactly (e.g., "games/doom.zip").</summary>
    ArchiveRoot,

    /// <summary>Path goes inside an archive (e.g., "games/doom.zip/maps").</summary>
    InsideArchive,

    /// <summary>Path does not exist.</summary>
    NotFound
}
