namespace ZipDrive.Domain.Configuration;

/// <summary>
/// Configuration settings for the mount system, bound from the "Mount" section of appsettings.jsonc.
/// Pure DTO with no framework dependencies — lives in Domain so all layers can reference it.
/// </summary>
public class MountSettings
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

    /// <summary>
    /// Fallback encoding name for non-UTF8 ZIP filenames when automatic detection fails.
    /// Accepts any .NET encoding name (e.g., "utf-8", "shift_jis", "gb2312").
    /// Default is "utf-8".
    /// </summary>
    public string FallbackEncoding { get; set; } = "utf-8";

    /// <summary>
    /// Minimum confidence threshold (0.0 to 1.0) for automatic charset detection.
    /// Detection results below this threshold are rejected. Default is 0.5.
    /// </summary>
    public float EncodingConfidenceThreshold { get; set; } = 0.3f;

    /// <summary>
    /// Quiet period in seconds for the FileSystemWatcher event consolidator.
    /// Events are batched and applied after this duration of silence.
    /// Lower values give faster response; higher values better handle bulk operations.
    /// Default is 5 seconds.
    /// </summary>
    public int DynamicReloadQuietPeriodSeconds { get; set; } = 5;
}
