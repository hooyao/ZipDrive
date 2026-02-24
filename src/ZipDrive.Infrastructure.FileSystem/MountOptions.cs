namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Configuration options for the Dokan mount, bound from the "Mount" section of appsettings.json.
/// </summary>
public class MountOptions
{
    /// <summary>
    /// Drive letter to mount (e.g., "R:\").
    /// </summary>
    public string MountPoint { get; set; } = @"R:\";

    /// <summary>
    /// Root directory containing ZIP archives to mount.
    /// </summary>
    public string ArchiveDirectory { get; set; } = "";

    /// <summary>
    /// Maximum directory depth for ZIP discovery (1-6).
    /// </summary>
    public int MaxDiscoveryDepth { get; set; } = 6;

    /// <summary>
    /// When true, short-circuits Windows shell metadata file probes (desktop.ini, Thumbs.db, etc.)
    /// to avoid unnecessarily parsing ZIP archives. Default is true.
    /// </summary>
    public bool ShortCircuitShellMetadata { get; set; } = true;
}
