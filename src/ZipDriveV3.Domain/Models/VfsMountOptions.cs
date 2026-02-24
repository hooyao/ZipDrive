namespace ZipDriveV3.Domain.Models;

/// <summary>
/// Options for mounting the virtual file system.
/// </summary>
public sealed class VfsMountOptions
{
    /// <summary>
    /// Root directory path to scan for ZIP files.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Maximum directory depth for ZIP discovery (1-6).
    /// Default: 6.
    /// </summary>
    public int MaxDiscoveryDepth { get; init; } = 6;
}
