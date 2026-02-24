namespace ZipDriveV3.Domain.Models;

public record ArchiveCapabilities(
    bool SupportsRandomAccess,
    bool SupportsPartialRead,
    bool SupportsWrite,
    bool SupportsEncryption
);
