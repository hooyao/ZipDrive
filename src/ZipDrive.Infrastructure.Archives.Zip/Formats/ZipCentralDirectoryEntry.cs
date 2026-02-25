using System.Text;

namespace ZipDrive.Infrastructure.Archives.Zip.Formats;

/// <summary>
/// Central Directory File Header entry.
/// Contains metadata about a single file/directory in the archive.
/// </summary>
/// <remarks>
/// <para>
/// ZIP format: 46 bytes fixed header + variable (filename, extra, comment).
/// </para>
/// <para>
/// This is the "streaming" unit - parsed one-by-one during Central Directory
/// enumeration without loading all entries into memory at once.
/// </para>
/// <para>
/// Field layout (46 bytes fixed):
/// <code>
/// Offset  Size  Field
/// 0       4     Signature (0x02014b50)
/// 4       2     Version made by
/// 6       2     Version needed to extract
/// 8       2     General purpose bit flag
/// 10      2     Compression method
/// 12      2     Last mod file time
/// 14      2     Last mod file date
/// 16      4     CRC-32
/// 20      4     Compressed size
/// 24      4     Uncompressed size
/// 28      2     File name length
/// 30      2     Extra field length
/// 32      2     File comment length
/// 34      2     Disk number start
/// 36      2     Internal file attributes
/// 38      4     External file attributes
/// 42      4     Relative offset of local header
/// 46      n     File name
/// 46+n    m     Extra field
/// 46+n+m  k     File comment
/// </code>
/// </para>
/// </remarks>
public readonly record struct ZipCentralDirectoryEntry
{
    #region Core Fields

    /// <summary>
    /// Version made by.
    /// </summary>
    /// <remarks>
    /// High byte: Host OS (0=DOS, 3=Unix, 10=NTFS, 19=macOS).
    /// Low byte: ZIP specification version (e.g., 20 = 2.0).
    /// </remarks>
    public required ushort VersionMadeBy { get; init; }

    /// <summary>
    /// Minimum ZIP version needed to extract.
    /// </summary>
    /// <remarks>
    /// Common values: 10 (1.0), 20 (2.0 for Deflate), 45 (4.5 for ZIP64).
    /// </remarks>
    public required ushort VersionNeededToExtract { get; init; }

    /// <summary>
    /// General purpose bit flags.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Bit 0: Encrypted</item>
    /// <item>Bit 1-2: Deflate compression options</item>
    /// <item>Bit 3: Data descriptor follows compressed data</item>
    /// <item>Bit 4: Enhanced deflating</item>
    /// <item>Bit 5: Compressed patched data</item>
    /// <item>Bit 6: Strong encryption</item>
    /// <item>Bit 11: UTF-8 filename encoding</item>
    /// <item>Bit 13: Central directory encrypted</item>
    /// </list>
    /// </remarks>
    public required ushort GeneralPurposeBitFlag { get; init; }

    /// <summary>
    /// Compression method.
    /// </summary>
    /// <remarks>
    /// Common values: 0 (Store), 8 (Deflate), 9 (Deflate64), 14 (LZMA).
    /// </remarks>
    public required ushort CompressionMethod { get; init; }

    /// <summary>
    /// Last modification time in MS-DOS format.
    /// </summary>
    /// <remarks>
    /// Bits 0-4: seconds/2, 5-10: minutes, 11-15: hours.
    /// </remarks>
    public required ushort LastModFileTime { get; init; }

    /// <summary>
    /// Last modification date in MS-DOS format.
    /// </summary>
    /// <remarks>
    /// Bits 0-4: day, 5-8: month, 9-15: year (since 1980).
    /// </remarks>
    public required ushort LastModFileDate { get; init; }

    /// <summary>
    /// CRC-32 checksum of uncompressed data.
    /// </summary>
    public required uint Crc32 { get; init; }

    /// <summary>
    /// Compressed size in bytes.
    /// </summary>
    /// <remarks>
    /// For ZIP64: If this field is 0xFFFFFFFF, the actual value is in the
    /// ZIP64 Extended Information Extra Field.
    /// </remarks>
    public required long CompressedSize { get; init; }

    /// <summary>
    /// Uncompressed size in bytes.
    /// </summary>
    /// <remarks>
    /// For ZIP64: If this field is 0xFFFFFFFF, the actual value is in the
    /// ZIP64 Extended Information Extra Field.
    /// </remarks>
    public required long UncompressedSize { get; init; }

    /// <summary>
    /// Raw filename bytes from the ZIP Central Directory.
    /// Always populated. Use <see cref="DecodeFileName"/> to get a string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses forward slashes (0x2F) as path separators per ZIP specification.
    /// Directories end with a trailing slash byte.
    /// </para>
    /// <para>
    /// Encoding depends on bit 11 of <see cref="GeneralPurposeBitFlag"/>:
    /// UTF-8 if set, otherwise a legacy code page (CP437, Shift-JIS, GBK, etc.).
    /// </para>
    /// </remarks>
    public required byte[] FileNameBytes { get; init; }

    /// <summary>
    /// Byte offset to the Local File Header for this entry.
    /// </summary>
    /// <remarks>
    /// For ZIP64: If this field is 0xFFFFFFFF, the actual value is in the
    /// ZIP64 Extended Information Extra Field.
    /// </remarks>
    public required long LocalHeaderOffset { get; init; }

    /// <summary>
    /// External file attributes (OS-specific).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For DOS/Windows (VersionMadeBy high byte = 0 or 10):
    /// Low byte contains DOS attributes (0x01=readonly, 0x02=hidden,
    /// 0x04=system, 0x10=directory, 0x20=archive).
    /// </para>
    /// <para>
    /// For Unix (VersionMadeBy high byte = 3):
    /// High 16 bits contain Unix permission mode (e.g., 0644, 0755).
    /// </para>
    /// </remarks>
    public required uint ExternalFileAttributes { get; init; }

    #endregion

    #region Derived Properties

    /// <summary>
    /// True if this entry represents a directory.
    /// </summary>
    /// <remarks>
    /// Determined by: filename bytes end with 0x2F ('/') OR DOS directory attribute is set.
    /// The '/' byte (0x2F) is encoding-agnostic — identical in CP437, Shift-JIS, GBK, EUC-KR, and UTF-8.
    /// </remarks>
    public bool IsDirectory =>
        (FileNameBytes.Length > 0 && FileNameBytes[^1] == (byte)'/') ||
        ((ExternalFileAttributes & ZipConstants.DosAttributeDirectory) != 0);

    /// <summary>
    /// Decodes the filename bytes using the specified encoding.
    /// </summary>
    /// <param name="encoding">
    /// Encoding to use. If null, defaults to UTF-8 when the UTF-8 flag (bit 11) is set,
    /// otherwise Code Page 437 (DOS).
    /// </param>
    /// <returns>The decoded filename string.</returns>
    public string DecodeFileName(Encoding? encoding = null)
    {
        encoding ??= IsUtf8 ? Encoding.UTF8 : Encoding.GetEncoding(437);
        return encoding.GetString(FileNameBytes);
    }

    /// <summary>
    /// Converts DOS date/time to DateTime.
    /// </summary>
    public DateTime LastModified => DosDateTimeToDateTime(LastModFileDate, LastModFileTime);

    /// <summary>
    /// True if filename is UTF-8 encoded (bit 11 of general purpose flag).
    /// </summary>
    public bool IsUtf8 => (GeneralPurposeBitFlag & ZipConstants.FlagUtf8) != 0;

    /// <summary>
    /// True if the entry is encrypted (bit 0 of general purpose flag).
    /// </summary>
    public bool IsEncrypted => (GeneralPurposeBitFlag & ZipConstants.FlagEncrypted) != 0;

    /// <summary>
    /// True if a data descriptor follows the compressed data (bit 3).
    /// </summary>
    public bool HasDataDescriptor => (GeneralPurposeBitFlag & ZipConstants.FlagDataDescriptor) != 0;

    /// <summary>
    /// Operating system that created this entry.
    /// </summary>
    public byte HostOs => (byte)(VersionMadeBy >> 8);

    #endregion

    #region Helper Methods

    /// <summary>
    /// Converts MS-DOS date and time to DateTime.
    /// </summary>
    private static DateTime DosDateTimeToDateTime(ushort date, ushort time)
    {
        // Date: bits 0-4 = day (1-31), 5-8 = month (1-12), 9-15 = year (since 1980)
        int day = date & 0x1F;
        int month = (date >> 5) & 0x0F;
        int year = ((date >> 9) & 0x7F) + 1980;

        // Time: bits 0-4 = seconds/2 (0-29), 5-10 = minutes (0-59), 11-15 = hours (0-23)
        int second = (time & 0x1F) * 2;
        int minute = (time >> 5) & 0x3F;
        int hour = (time >> 11) & 0x1F;

        // Validate ranges to avoid exceptions from invalid DOS timestamps
        if (month < 1 || month > 12) month = 1;
        if (day < 1 || day > 31) day = 1;
        if (hour > 23) hour = 0;
        if (minute > 59) minute = 0;
        if (second > 59) second = 0;

        try
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Fall back to minimum date for truly invalid timestamps
            return DateTime.MinValue;
        }
    }

    #endregion
}
