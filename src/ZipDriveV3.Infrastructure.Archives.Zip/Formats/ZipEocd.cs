namespace ZipDriveV3.Infrastructure.Archives.Zip.Formats;

/// <summary>
/// End of Central Directory Record (EOCD).
/// Located at the end of the ZIP file, contains pointers to the Central Directory.
/// </summary>
/// <remarks>
/// <para>
/// ZIP format: 22 bytes minimum (+ variable comment length).
/// </para>
/// <para>
/// ZIP64: When entry count exceeds 65535 or sizes exceed 4GB, the standard EOCD
/// fields are set to 0xFFFF/0xFFFFFFFF and actual values are stored in the
/// ZIP64 End of Central Directory Record.
/// </para>
/// <para>
/// File layout at end of ZIP:
/// <code>
/// [ZIP64 EOCD] (optional, 56+ bytes)
/// [ZIP64 EOCD Locator] (optional, 20 bytes)
/// [Standard EOCD] (22+ bytes) ← Always present
/// </code>
/// </para>
/// </remarks>
public readonly record struct ZipEocd
{
    /// <summary>
    /// Number of entries in the Central Directory.
    /// </summary>
    /// <remarks>
    /// For ZIP64 archives where entry count exceeds 65535, this value is read
    /// from the ZIP64 EOCD record.
    /// </remarks>
    public required long EntryCount { get; init; }

    /// <summary>
    /// Size of the Central Directory in bytes.
    /// </summary>
    /// <remarks>
    /// For ZIP64 archives where CD size exceeds 4GB, this value is read
    /// from the ZIP64 EOCD record.
    /// </remarks>
    public required long CentralDirectorySize { get; init; }

    /// <summary>
    /// Byte offset of the Central Directory from the start of the archive.
    /// </summary>
    /// <remarks>
    /// For ZIP64 archives where offset exceeds 4GB, this value is read
    /// from the ZIP64 EOCD record.
    /// </remarks>
    public required long CentralDirectoryOffset { get; init; }

    /// <summary>
    /// True if this is a ZIP64 archive.
    /// </summary>
    /// <remarks>
    /// ZIP64 is used when:
    /// <list type="bullet">
    /// <item>Entry count exceeds 65535</item>
    /// <item>File size exceeds 4GB</item>
    /// <item>Central Directory offset exceeds 4GB</item>
    /// <item>Central Directory size exceeds 4GB</item>
    /// </list>
    /// </remarks>
    public required bool IsZip64 { get; init; }

    /// <summary>
    /// Absolute byte position of the EOCD signature in the file.
    /// </summary>
    /// <remarks>
    /// Used for validation and calculating adjustments for self-extracting archives.
    /// </remarks>
    public required long EocdPosition { get; init; }

    /// <summary>
    /// Archive comment (usually empty).
    /// </summary>
    /// <remarks>
    /// Maximum length is 65535 bytes. Most ZIP files have no comment.
    /// We store this for completeness but don't use it.
    /// </remarks>
    public string? Comment { get; init; }

    /// <summary>
    /// Disk number containing this EOCD.
    /// </summary>
    /// <remarks>
    /// For single-disk archives (most common), this is 0.
    /// Multi-disk spanning is not supported.
    /// </remarks>
    public ushort DiskNumber { get; init; }

    /// <summary>
    /// Disk number where the Central Directory starts.
    /// </summary>
    /// <remarks>
    /// For single-disk archives (most common), this is 0.
    /// Multi-disk spanning is not supported.
    /// </remarks>
    public ushort CentralDirectoryDiskNumber { get; init; }
}
