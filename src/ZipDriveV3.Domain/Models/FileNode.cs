namespace ZipDriveV3.Domain.Models;

public record FileNode(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastModified,
    IReadOnlyList<FileNode> Children
)
{
    public static FileNode CreateRoot() =>
        new FileNode("", "", true, 0, DateTime.UtcNow, Array.Empty<FileNode>());
}
