namespace ZipDriveV3.Domain.Models;

/// <summary>
/// Minimal metadata for a ZIP entry, optimized for extraction.
/// Stored in the structure cache - one per file in archive.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Memory footprint:</strong> ~40 bytes (struct, no heap allocation for entry itself).
/// For a 10,000-file ZIP: ~400 KB structure cache (plus file name strings).
/// </para>
/// <para>
/// This struct contains only the fields needed to:
/// <list type="number">
/// <item>Seek to the correct position in the ZIP file</item>
/// <item>Read the correct number of bytes</item>
/// <item>Decompress using the correct method</item>
/// <item>Display file information in directory listings</item>
/// </list>
/// </para>
/// </remarks>
public readonly record struct ZipEntryInfo
{
    /// <summary>
    /// Absolute byte offset to the Local File Header in the ZIP file.
    /// Used as the seek position for extraction.
    /// </summary>
    /// <remarks>
    /// The actual compressed data starts at:
    /// <c>LocalHeaderOffset + 30 + FileNameLength + ExtraFieldLength</c>
    /// </remarks>
    public required long LocalHeaderOffset { get; init; }

    /// <summary>
    /// Size of compressed data in bytes.
    /// </summary>
    /// <remarks>
    /// This is the number of bytes to read after the Local Header.
    /// For Store compression (method 0), this equals UncompressedSize.
    /// </remarks>
    public required long CompressedSize { get; init; }

    /// <summary>
    /// Size of uncompressed data in bytes.
    /// </summary>
    /// <remarks>
    /// Used for:
    /// <list type="bullet">
    /// <item>Output buffer allocation</item>
    /// <item>Progress reporting</item>
    /// <item>Validation after decompression</item>
    /// <item>File size display in directory listings</item>
    /// </list>
    /// </remarks>
    public required long UncompressedSize { get; init; }

    /// <summary>
    /// ZIP compression method.
    /// </summary>
    /// <remarks>
    /// Supported values:
    /// <list type="bullet">
    /// <item>0 = Store (no compression)</item>
    /// <item>8 = Deflate</item>
    /// </list>
    /// </remarks>
    public required ushort CompressionMethod { get; init; }

    /// <summary>
    /// True if this entry represents a directory.
    /// </summary>
    public required bool IsDirectory { get; init; }

    /// <summary>
    /// Last modification timestamp.
    /// </summary>
    /// <remarks>
    /// Converted from MS-DOS date/time format in the ZIP file.
    /// </remarks>
    public required DateTime LastModified { get; init; }

    /// <summary>
    /// File attributes for Windows Explorer display.
    /// </summary>
    public required FileAttributes Attributes { get; init; }

    /// <summary>
    /// CRC-32 checksum for integrity validation.
    /// </summary>
    /// <remarks>
    /// Can be used to verify extracted data integrity.
    /// </remarks>
    public uint Crc32 { get; init; }

    /// <summary>
    /// True if the entry is encrypted.
    /// </summary>
    /// <remarks>
    /// Encrypted entries cannot be extracted without a password.
    /// Password support is not implemented in V3.
    /// </remarks>
    public bool IsEncrypted { get; init; }

    /// <summary>
    /// Compression ratio as a percentage (0-100).
    /// </summary>
    /// <remarks>
    /// Higher values indicate better compression.
    /// Returns 0 for empty files or directories.
    /// </remarks>
    public double CompressionRatio =>
        UncompressedSize == 0
            ? 0
            : Math.Round((1.0 - (double)CompressedSize / UncompressedSize) * 100, 1);
}
