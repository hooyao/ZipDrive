namespace ZipDrive.Domain.Models;

public record ArchiveCapabilities(
    bool SupportsRandomAccess,
    bool SupportsPartialRead,
    bool SupportsWrite,
    bool SupportsEncryption
);
