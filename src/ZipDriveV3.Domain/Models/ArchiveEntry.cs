namespace ZipDriveV3.Domain.Models;

public record ArchiveEntry(
    string FullPath,
    string Name,
    long CompressedSize,
    long UncompressedSize,
    DateTime LastModified,
    bool IsDirectory
);
