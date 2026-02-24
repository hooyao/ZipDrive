namespace ZipDrive.Infrastructure.Archives.Zip.Formats;

/// <summary>
/// ZIP format constants and signatures.
/// All values are little-endian as per ZIP specification (PKWARE APPNOTE.TXT).
/// </summary>
public static class ZipConstants
{
    #region Signatures

    /// <summary>
    /// Local File Header signature: "PK\x03\x04" (0x04034b50 little-endian).
    /// </summary>
    public const uint LocalFileHeaderSignature = 0x04034b50;

    /// <summary>
    /// Central Directory File Header signature: "PK\x01\x02" (0x02014b50 little-endian).
    /// </summary>
    public const uint CentralDirectoryHeaderSignature = 0x02014b50;

    /// <summary>
    /// End of Central Directory Record signature: "PK\x05\x06" (0x06054b50 little-endian).
    /// </summary>
    public const uint EndOfCentralDirectorySignature = 0x06054b50;

    /// <summary>
    /// ZIP64 End of Central Directory Record signature: "PK\x06\x06" (0x06064b50 little-endian).
    /// </summary>
    public const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;

    /// <summary>
    /// ZIP64 End of Central Directory Locator signature: "PK\x06\x07" (0x07064b50 little-endian).
    /// </summary>
    public const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064b50;

    /// <summary>
    /// Data Descriptor signature (optional): "PK\x07\x08" (0x08074b50 little-endian).
    /// </summary>
    public const uint DataDescriptorSignature = 0x08074b50;

    #endregion

    #region Header Sizes

    /// <summary>
    /// Fixed size of Local File Header (excluding variable-length fields).
    /// </summary>
    public const int LocalFileHeaderFixedSize = 30;

    /// <summary>
    /// Fixed size of Central Directory File Header (excluding variable-length fields).
    /// </summary>
    public const int CentralDirectoryHeaderFixedSize = 46;

    /// <summary>
    /// Minimum size of End of Central Directory Record (excluding comment).
    /// </summary>
    public const int EndOfCentralDirectoryMinSize = 22;

    /// <summary>
    /// Size of ZIP64 End of Central Directory Locator.
    /// </summary>
    public const int Zip64EndOfCentralDirectoryLocatorSize = 20;

    /// <summary>
    /// Minimum size of ZIP64 End of Central Directory Record.
    /// </summary>
    public const int Zip64EndOfCentralDirectoryMinSize = 56;

    #endregion

    #region Compression Methods

    /// <summary>
    /// No compression (Store).
    /// </summary>
    public const ushort CompressionMethodStore = 0;

    /// <summary>
    /// Deflate compression.
    /// </summary>
    public const ushort CompressionMethodDeflate = 8;

    /// <summary>
    /// Deflate64 compression (not supported).
    /// </summary>
    public const ushort CompressionMethodDeflate64 = 9;

    /// <summary>
    /// BZIP2 compression (not supported).
    /// </summary>
    public const ushort CompressionMethodBzip2 = 12;

    /// <summary>
    /// LZMA compression (not supported).
    /// </summary>
    public const ushort CompressionMethodLzma = 14;

    /// <summary>
    /// Zstandard compression (not supported).
    /// </summary>
    public const ushort CompressionMethodZstd = 93;

    #endregion

    #region General Purpose Bit Flags

    /// <summary>
    /// Bit 0: File is encrypted.
    /// </summary>
    public const ushort FlagEncrypted = 0x0001;

    /// <summary>
    /// Bit 3: Data descriptor follows compressed data (CRC/sizes in local header are 0).
    /// </summary>
    public const ushort FlagDataDescriptor = 0x0008;

    /// <summary>
    /// Bit 11: Language encoding flag (UTF-8).
    /// </summary>
    public const ushort FlagUtf8 = 0x0800;

    #endregion

    #region ZIP64 Markers

    /// <summary>
    /// Marker value indicating ZIP64 extended information is needed for 32-bit fields.
    /// </summary>
    public const uint Zip64Marker32 = 0xFFFFFFFF;

    /// <summary>
    /// Marker value indicating ZIP64 extended information is needed for 16-bit fields.
    /// </summary>
    public const ushort Zip64Marker16 = 0xFFFF;

    /// <summary>
    /// ZIP64 Extended Information Extra Field header ID.
    /// </summary>
    public const ushort Zip64ExtraFieldHeaderId = 0x0001;

    #endregion

    #region Version Made By - OS Codes (High Byte)

    /// <summary>
    /// MS-DOS / OS/2 (FAT filesystem).
    /// </summary>
    public const byte OsMsDos = 0;

    /// <summary>
    /// Unix.
    /// </summary>
    public const byte OsUnix = 3;

    /// <summary>
    /// Windows NTFS.
    /// </summary>
    public const byte OsNtfs = 10;

    /// <summary>
    /// macOS (Darwin).
    /// </summary>
    public const byte OsMacOs = 19;

    #endregion

    #region DOS File Attributes

    /// <summary>
    /// Read-only file attribute.
    /// </summary>
    public const byte DosAttributeReadOnly = 0x01;

    /// <summary>
    /// Hidden file attribute.
    /// </summary>
    public const byte DosAttributeHidden = 0x02;

    /// <summary>
    /// System file attribute.
    /// </summary>
    public const byte DosAttributeSystem = 0x04;

    /// <summary>
    /// Directory attribute.
    /// </summary>
    public const byte DosAttributeDirectory = 0x10;

    /// <summary>
    /// Archive file attribute.
    /// </summary>
    public const byte DosAttributeArchive = 0x20;

    #endregion

    #region Limits

    /// <summary>
    /// Maximum comment length in ZIP file (65535 bytes).
    /// </summary>
    public const int MaxCommentLength = 65535;

    /// <summary>
    /// Maximum search range for EOCD from end of file.
    /// </summary>
    public const int MaxEocdSearchRange = EndOfCentralDirectoryMinSize + MaxCommentLength;

    #endregion
}
