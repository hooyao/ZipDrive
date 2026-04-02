namespace ZipDrive.Domain.Models;

/// <summary>
/// Format-agnostic metadata for an archive entry.
/// Contains only consumer-facing fields needed for:
/// - File size display and EOF checks (UncompressedSize)
/// - Directory listing (IsDirectory, LastModified, Attributes)
/// - Error reporting (IsEncrypted)
/// - Integrity verification (Checksum)
///
/// Format-specific extraction metadata (offsets, compression methods, block indices)
/// stays in each format's provider project, accessed via the entry's internal path.
/// </summary>
public readonly record struct ArchiveEntryInfo
{
    /// <summary>
    /// Decompressed file size in bytes. Used for:
    /// - Cache tier routing (memory vs disk)
    /// - EOF checks in ReadAsync
    /// - Buffer allocation
    /// - File size display in directory listings
    /// </summary>
    public required long UncompressedSize { get; init; }

    /// <summary>True if this entry represents a directory.</summary>
    public required bool IsDirectory { get; init; }

    /// <summary>Last modification timestamp.</summary>
    public required DateTime LastModified { get; init; }

    /// <summary>File attributes for Windows Explorer display.</summary>
    public required FileAttributes Attributes { get; init; }

    /// <summary>True if the entry is encrypted (cannot extract without password).</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>
    /// Integrity checksum. Semantics are format-defined:
    /// ZIP and RAR use CRC-32. Zero if not available.
    /// </summary>
    public uint Checksum { get; init; }
}
