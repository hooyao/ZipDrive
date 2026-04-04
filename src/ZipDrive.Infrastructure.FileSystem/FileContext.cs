using System.Runtime.Versioning;

namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Per-open-handle context passed through WinFsp's FileNode/FileDesc parameters.
/// Carries the virtual path and directory flag resolved at Open time.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FileContext
{
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
}
