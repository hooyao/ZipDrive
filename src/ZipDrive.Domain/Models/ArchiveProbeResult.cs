namespace ZipDrive.Domain.Models;

/// <summary>
/// Result of a lightweight probe to detect unsupported archive variants
/// (e.g., solid RAR) before trie registration.
/// </summary>
/// <param name="IsSupported">True if the archive can be mounted normally.</param>
/// <param name="UnsupportedReason">Human-readable reason when not supported. Null if supported.</param>
public sealed record ArchiveProbeResult(bool IsSupported, string? UnsupportedReason = null)
{
    /// <summary>
    /// Suffix appended to the virtual path of unsupported archives so users see them
    /// immediately in Explorer without clicking in.
    /// </summary>
    public const string UnsupportedFolderSuffix = " (NOT SUPPORTED)";
}
