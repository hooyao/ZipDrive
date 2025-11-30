using ZipDriveV3.Domain.Abstractions;

namespace ZipDriveV3.Domain.Models;

public record ArchiveDescriptor(
    string ArchiveKey,
    string FilePath,
    IArchiveSession Session,
    DateTime RegisteredAt
);
