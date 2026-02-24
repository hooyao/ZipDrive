namespace ZipDriveV3.Domain.Models;

public record PathResolutionResult(
    string? ArchiveKey,
    string InternalPath,
    PathResolutionStatus Status
);

public enum PathResolutionStatus
{
    Success,
    RootDirectory,         // "\" → list all archives
    ArchiveRoot,           // "\archive.zip\" → list archive contents
    NotFound,
    InvalidPath
}
