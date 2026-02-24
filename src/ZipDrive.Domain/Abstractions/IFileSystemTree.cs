using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// In-memory tree structure representing archive contents
/// </summary>
public interface IFileSystemTree
{
    /// <summary>Root node</summary>
    FileNode Root { get; }

    /// <summary>
    /// Finds a node by path
    /// </summary>
    FileNode? FindNode(string path);

    /// <summary>
    /// Lists children of a directory
    /// </summary>
    IReadOnlyCollection<FileNode> ListChildren(string directoryPath);
}
