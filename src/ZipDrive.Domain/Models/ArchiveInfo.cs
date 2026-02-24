namespace ZipDrive.Domain.Models;

public record ArchiveInfo(
    string FilePath,
    string ArchiveKey,
    long SizeBytes,
    DateTime LastModified,
    string FormatId
);
