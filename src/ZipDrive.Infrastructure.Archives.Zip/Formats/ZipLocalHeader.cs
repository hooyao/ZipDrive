namespace ZipDrive.Infrastructure.Archives.Zip.Formats;

/// <summary>
/// Local File Header - precedes each file's compressed data.
/// </summary>
/// <remarks>
/// <para>
/// ZIP format: 30 bytes fixed header + variable (filename, extra field).
/// Compressed data immediately follows the extra field.
/// </para>
/// <para>
/// This structure is used during extraction to calculate the exact offset
/// where compressed data begins. We read the Local Header to get the
/// variable-length field sizes (which may differ from the Central Directory).
/// </para>
/// <para>
/// Field layout (30 bytes fixed):
/// <code>
/// Offset  Size  Field
/// 0       4     Signature (0x04034b50)
/// 4       2     Version needed to extract
/// 6       2     General purpose bit flag
/// 8       2     Compression method
/// 10      2     Last mod file time
/// 12      2     Last mod file date
/// 14      4     CRC-32
/// 18      4     Compressed size
/// 22      4     Uncompressed size
/// 26      2     File name length
/// 28      2     Extra field length
/// 30      n     File name
/// 30+n    m     Extra field
/// 30+n+m  ...   Compressed data starts here
/// </code>
/// </para>
/// </remarks>
public readonly record struct ZipLocalHeader
{
    /// <summary>
    /// Length of file name field in bytes.
    /// </summary>
    public required ushort FileNameLength { get; init; }

    /// <summary>
    /// Length of extra field in bytes.
    /// </summary>
    /// <remarks>
    /// The extra field in the Local Header may differ from the Central Directory.
    /// Some tools store different extra field data in each location.
    /// </remarks>
    public required ushort ExtraFieldLength { get; init; }

    /// <summary>
    /// Compression method.
    /// </summary>
    /// <remarks>
    /// Should match the value in the Central Directory entry, but we read it
    /// here for validation purposes.
    /// </remarks>
    public required ushort CompressionMethod { get; init; }

    /// <summary>
    /// General purpose bit flags.
    /// </summary>
    /// <remarks>
    /// Should match the value in the Central Directory entry.
    /// </remarks>
    public required ushort GeneralPurposeBitFlag { get; init; }

    /// <summary>
    /// CRC-32 from Local Header.
    /// </summary>
    /// <remarks>
    /// May be 0 if bit 3 (data descriptor) is set in flags.
    /// </remarks>
    public uint Crc32 { get; init; }

    /// <summary>
    /// Compressed size from Local Header.
    /// </summary>
    /// <remarks>
    /// May be 0 if bit 3 (data descriptor) is set in flags.
    /// For extraction, we use the size from Central Directory instead.
    /// </remarks>
    public uint CompressedSize { get; init; }

    /// <summary>
    /// Uncompressed size from Local Header.
    /// </summary>
    /// <remarks>
    /// May be 0 if bit 3 (data descriptor) is set in flags.
    /// For extraction, we use the size from Central Directory instead.
    /// </remarks>
    public uint UncompressedSize { get; init; }

    /// <summary>
    /// Total size of the Local Header including variable-length fields.
    /// </summary>
    /// <remarks>
    /// This is the offset from LocalHeaderOffset (from Central Directory)
    /// to the start of compressed data.
    /// </remarks>
    public int TotalHeaderSize => ZipConstants.LocalFileHeaderFixedSize + FileNameLength + ExtraFieldLength;

    /// <summary>
    /// True if the entry is encrypted (bit 0 of general purpose flag).
    /// </summary>
    public bool IsEncrypted => (GeneralPurposeBitFlag & ZipConstants.FlagEncrypted) != 0;

    /// <summary>
    /// True if a data descriptor follows the compressed data (bit 3).
    /// </summary>
    /// <remarks>
    /// When set, CRC-32 and sizes in this header are 0, and actual values
    /// appear in a Data Descriptor after the compressed data.
    /// </remarks>
    public bool HasDataDescriptor => (GeneralPurposeBitFlag & ZipConstants.FlagDataDescriptor) != 0;
}
