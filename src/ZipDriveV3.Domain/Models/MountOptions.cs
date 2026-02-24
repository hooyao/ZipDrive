namespace ZipDriveV3.Domain.Models;

public record MountOptions(
    string MountPath,              // e.g., "R:\"
    string? SingleArchivePath,     // Single file mode
    string? ArchiveFolder,         // Multi-archive mode
    bool WatchForChanges
);
