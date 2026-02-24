using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using ZipDriveV3.Domain.Exceptions;
using ZipDriveV3.Domain.Models;
using ZipDriveV3.Infrastructure.Archives.Zip.Formats;

namespace ZipDriveV3.Infrastructure.Archives.Zip;

/// <summary>
/// Streaming ZIP archive reader implementation.
/// </summary>
/// <remarks>
/// <para>
/// This implementation parses ZIP archives efficiently with streaming Central Directory
/// enumeration via <see cref="IAsyncEnumerable{T}"/>. No bulk allocation of entry lists
/// is performed - entries are yielded one at a time.
/// </para>
/// <para>
/// <strong>Supported Features:</strong>
/// <list type="bullet">
/// <item>Standard ZIP (32-bit)</item>
/// <item>ZIP64 (64-bit sizes and offsets)</item>
/// <item>Store compression (method 0)</item>
/// <item>Deflate compression (method 8)</item>
/// <item>UTF-8 and CP437 filename encoding</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ZipReader : IZipReader
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly ILogger<ZipReader>? _logger;
    private readonly Encoding _fallbackEncoding;

    private bool _disposed;

    /// <summary>
    /// Creates a new ZipReader for the specified stream.
    /// </summary>
    /// <param name="stream">Stream to read from (must support seeking and reading).</param>
    /// <param name="leaveOpen">If true, the stream is not disposed when this reader is disposed.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="fallbackEncoding">
    /// Encoding to use for filenames when UTF-8 flag is not set.
    /// Defaults to Code Page 437 (DOS).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException">Stream does not support seeking or reading.</exception>
    public ZipReader(
        Stream stream,
        bool leaveOpen = false,
        ILogger<ZipReader>? logger = null,
        Encoding? fallbackEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
            throw new ArgumentException("Stream must support seeking.", nameof(stream));
        if (!stream.CanRead)
            throw new ArgumentException("Stream must support reading.", nameof(stream));

        _stream = stream;
        _leaveOpen = leaveOpen;
        _logger = logger;

        // Default to CP437 (DOS encoding) for non-UTF8 filenames
        // Use 936 (Simplified Chinese) as a common alternative for Asian archives
        _fallbackEncoding = fallbackEncoding ?? Encoding.GetEncoding(437);

        // Try to get file path from FileStream
        if (stream is FileStream fs)
        {
            FilePath = fs.Name;
        }
    }

    /// <summary>
    /// Creates a new ZipReader for a file.
    /// </summary>
    /// <param name="filePath">Path to the ZIP file.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public ZipReader(string filePath, ILogger<ZipReader>? logger = null)
        : this(
            new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true),
            leaveOpen: false,
            logger)
    {
        FilePath = filePath;
    }

    /// <inheritdoc />
    public long StreamLength => _stream.Length;

    /// <inheritdoc />
    public string? FilePath { get; }

    #region EOCD Reading

    /// <inheritdoc />
    public async Task<ZipEocd> ReadEocdAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long fileLength = _stream.Length;

        // Minimum file size check
        if (fileLength < ZipConstants.EndOfCentralDirectoryMinSize)
        {
            throw new CorruptZipException(
                $"File is too small ({fileLength} bytes) to be a valid ZIP archive. " +
                $"Minimum size is {ZipConstants.EndOfCentralDirectoryMinSize} bytes.",
                FilePath);
        }

        // Search for EOCD signature from end of file
        // Max search range = EOCD size + max comment length
        int searchRange = (int)Math.Min(ZipConstants.MaxEocdSearchRange, fileLength);
        long searchStart = fileLength - searchRange;

        byte[] buffer = new byte[searchRange];
        _stream.Seek(searchStart, SeekOrigin.Begin);
        await _stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

        // Search backwards for EOCD signature
        int eocdOffset = FindEocdSignature(buffer);
        if (eocdOffset < 0)
        {
            throw new EocdNotFoundException(FilePath);
        }

        long eocdPosition = searchStart + eocdOffset;
        _logger?.LogDebug("Found EOCD at position {Position}", eocdPosition);

        // Parse standard EOCD
        ZipEocd eocd = ParseStandardEocd(buffer.AsSpan(eocdOffset), eocdPosition);

        // Check if ZIP64 is needed
        if (NeedsZip64(eocd))
        {
            _logger?.LogDebug("ZIP64 format detected, reading ZIP64 EOCD");
            eocd = await ReadZip64EocdAsync(eocdPosition, cancellationToken).ConfigureAwait(false);
        }

        // Validate EOCD
        ValidateEocd(eocd);

        _logger?.LogInformation(
            "Parsed EOCD: {EntryCount} entries, CD at offset {Offset}, size {Size}, ZIP64={IsZip64}",
            eocd.EntryCount, eocd.CentralDirectoryOffset, eocd.CentralDirectorySize, eocd.IsZip64);

        return eocd;
    }

    private static int FindEocdSignature(ReadOnlySpan<byte> buffer)
    {
        // Search backwards from end for EOCD signature
        for (int i = buffer.Length - ZipConstants.EndOfCentralDirectoryMinSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[i..]) == ZipConstants.EndOfCentralDirectorySignature)
            {
                return i;
            }
        }
        return -1;
    }

    private static ZipEocd ParseStandardEocd(ReadOnlySpan<byte> data, long eocdPosition)
    {
        // Validate signature
        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (signature != ZipConstants.EndOfCentralDirectorySignature)
        {
            throw new InvalidSignatureException(
                ZipConstants.EndOfCentralDirectorySignature,
                signature,
                "End of Central Directory",
                offset: eocdPosition);
        }

        ushort diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        ushort cdDiskNumber = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        ushort entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        ushort totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        uint cdSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        uint cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(data[20..]);

        string? comment = null;
        if (commentLength > 0 && data.Length >= 22 + commentLength)
        {
            comment = Encoding.UTF8.GetString(data.Slice(22, commentLength));
        }

        return new ZipEocd
        {
            DiskNumber = diskNumber,
            CentralDirectoryDiskNumber = cdDiskNumber,
            EntryCount = totalEntries,
            CentralDirectorySize = cdSize,
            CentralDirectoryOffset = cdOffset,
            IsZip64 = false,
            EocdPosition = eocdPosition,
            Comment = comment
        };
    }

    private static bool NeedsZip64(ZipEocd eocd)
    {
        return eocd.EntryCount == ZipConstants.Zip64Marker16 ||
               eocd.CentralDirectorySize == ZipConstants.Zip64Marker32 ||
               eocd.CentralDirectoryOffset == ZipConstants.Zip64Marker32 ||
               eocd.DiskNumber == ZipConstants.Zip64Marker16;
    }

    private async Task<ZipEocd> ReadZip64EocdAsync(long standardEocdPosition, CancellationToken cancellationToken)
    {
        // ZIP64 EOCD Locator is immediately before the standard EOCD
        long locatorPosition = standardEocdPosition - ZipConstants.Zip64EndOfCentralDirectoryLocatorSize;
        if (locatorPosition < 0)
        {
            throw new Zip64RequiredException("End of Central Directory Locator", FilePath);
        }

        // Read ZIP64 EOCD Locator
        byte[] locatorBuffer = new byte[ZipConstants.Zip64EndOfCentralDirectoryLocatorSize];
        _stream.Seek(locatorPosition, SeekOrigin.Begin);
        await _stream.ReadExactlyAsync(locatorBuffer, cancellationToken).ConfigureAwait(false);

        uint locatorSignature = BinaryPrimitives.ReadUInt32LittleEndian(locatorBuffer);
        if (locatorSignature != ZipConstants.Zip64EndOfCentralDirectoryLocatorSignature)
        {
            throw new Zip64RequiredException("End of Central Directory Locator", FilePath);
        }

        // Get ZIP64 EOCD offset from locator
        long zip64EocdOffset = BinaryPrimitives.ReadInt64LittleEndian(locatorBuffer.AsSpan(8));

        // Read ZIP64 EOCD
        byte[] zip64Buffer = new byte[ZipConstants.Zip64EndOfCentralDirectoryMinSize];
        _stream.Seek(zip64EocdOffset, SeekOrigin.Begin);
        await _stream.ReadExactlyAsync(zip64Buffer, cancellationToken).ConfigureAwait(false);

        uint zip64Signature = BinaryPrimitives.ReadUInt32LittleEndian(zip64Buffer);
        if (zip64Signature != ZipConstants.Zip64EndOfCentralDirectorySignature)
        {
            throw new InvalidSignatureException(
                ZipConstants.Zip64EndOfCentralDirectorySignature,
                zip64Signature,
                "ZIP64 End of Central Directory",
                FilePath,
                zip64EocdOffset);
        }

        // Parse ZIP64 EOCD fields
        uint diskNumber = BinaryPrimitives.ReadUInt32LittleEndian(zip64Buffer.AsSpan(16));
        uint cdDiskNumber = BinaryPrimitives.ReadUInt32LittleEndian(zip64Buffer.AsSpan(20));
        long entriesOnDisk = BinaryPrimitives.ReadInt64LittleEndian(zip64Buffer.AsSpan(24));
        long totalEntries = BinaryPrimitives.ReadInt64LittleEndian(zip64Buffer.AsSpan(32));
        long cdSize = BinaryPrimitives.ReadInt64LittleEndian(zip64Buffer.AsSpan(40));
        long cdOffset = BinaryPrimitives.ReadInt64LittleEndian(zip64Buffer.AsSpan(48));

        return new ZipEocd
        {
            DiskNumber = (ushort)Math.Min(diskNumber, ushort.MaxValue),
            CentralDirectoryDiskNumber = (ushort)Math.Min(cdDiskNumber, ushort.MaxValue),
            EntryCount = totalEntries,
            CentralDirectorySize = cdSize,
            CentralDirectoryOffset = cdOffset,
            IsZip64 = true,
            EocdPosition = standardEocdPosition
        };
    }

    private void ValidateEocd(ZipEocd eocd)
    {
        // Check for multi-disk archives
        if (eocd.DiskNumber != 0 || eocd.CentralDirectoryDiskNumber != 0)
        {
            throw new MultiDiskArchiveException(FilePath);
        }

        // Validate Central Directory offset
        if (eocd.CentralDirectoryOffset > _stream.Length)
        {
            throw new InvalidOffsetException(
                eocd.CentralDirectoryOffset,
                _stream.Length,
                "Central Directory",
                FilePath);
        }

        // Validate Central Directory fits within file
        if (eocd.CentralDirectoryOffset + eocd.CentralDirectorySize > _stream.Length)
        {
            throw new CorruptZipException(
                $"Central Directory extends beyond file end. Offset: {eocd.CentralDirectoryOffset}, " +
                $"Size: {eocd.CentralDirectorySize}, File size: {_stream.Length}",
                FilePath);
        }
    }

    #endregion

    #region Central Directory Streaming

    /// <inheritdoc />
    public async IAsyncEnumerable<ZipCentralDirectoryEntry> StreamCentralDirectoryAsync(
        ZipEocd eocd,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Seek to Central Directory
        _stream.Seek(eocd.CentralDirectoryOffset, SeekOrigin.Begin);

        // Reusable buffer for fixed header portion (46 bytes)
        byte[] headerBuffer = new byte[ZipConstants.CentralDirectoryHeaderFixedSize];
        long entriesYielded = 0;

        while (entriesYielded < eocd.EntryCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Read fixed header
            try
            {
                await _stream.ReadExactlyAsync(headerBuffer, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                throw new CorruptZipException(
                    $"Unexpected end of file after {entriesYielded} entries (expected {eocd.EntryCount}). " +
                    "The Central Directory may be truncated.",
                    FilePath,
                    _stream.Position);
            }

            // Validate signature
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer);
            if (signature != ZipConstants.CentralDirectoryHeaderSignature)
            {
                throw new InvalidSignatureException(
                    ZipConstants.CentralDirectoryHeaderSignature,
                    signature,
                    $"Central Directory entry {entriesYielded}",
                    FilePath,
                    _stream.Position - ZipConstants.CentralDirectoryHeaderFixedSize);
            }

            // Parse fixed fields
            ZipCentralDirectoryEntry entry = ParseCentralDirectoryHeader(headerBuffer);

            // Read variable-length fields
            ushort fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(28));
            ushort extraFieldLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(30));
            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(32));

            // Read filename
            byte[] fileNameBytes = new byte[fileNameLength];
            await _stream.ReadExactlyAsync(fileNameBytes, cancellationToken).ConfigureAwait(false);

            // Decode filename
            Encoding encoding = entry.IsUtf8 ? Encoding.UTF8 : _fallbackEncoding;
            string fileName = encoding.GetString(fileNameBytes);
            entry = entry with { FileName = fileName };

            // Read extra field (may contain ZIP64 info)
            if (extraFieldLength > 0)
            {
                byte[] extraField = new byte[extraFieldLength];
                await _stream.ReadExactlyAsync(extraField, cancellationToken).ConfigureAwait(false);

                // Parse ZIP64 extended info if needed
                if (entry.CompressedSize == ZipConstants.Zip64Marker32 ||
                    entry.UncompressedSize == ZipConstants.Zip64Marker32 ||
                    entry.LocalHeaderOffset == ZipConstants.Zip64Marker32)
                {
                    entry = ParseZip64ExtraField(entry, extraField);
                }
            }

            // Skip comment (rarely used)
            if (commentLength > 0)
            {
                _stream.Seek(commentLength, SeekOrigin.Current);
            }

            entriesYielded++;

            // YIELD IMMEDIATELY - this is the key to streaming!
            yield return entry;
        }

        _logger?.LogDebug("Streamed {Count} Central Directory entries", entriesYielded);
    }

    private static ZipCentralDirectoryEntry ParseCentralDirectoryHeader(ReadOnlySpan<byte> data)
    {
        return new ZipCentralDirectoryEntry
        {
            VersionMadeBy = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            VersionNeededToExtract = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]),
            GeneralPurposeBitFlag = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            CompressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]),
            LastModFileTime = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
            LastModFileDate = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
            Crc32 = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            CompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            UncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]),
            // fileNameLength at 28, extraFieldLength at 30, commentLength at 32 - handled separately
            // diskNumberStart at 34 - ignored (single disk only)
            // internalFileAttributes at 36 - ignored
            ExternalFileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(data[38..]),
            LocalHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[42..]),
            FileName = "" // Will be set after reading variable-length fields
        };
    }

    private static ZipCentralDirectoryEntry ParseZip64ExtraField(
        ZipCentralDirectoryEntry entry,
        ReadOnlySpan<byte> extraField)
    {
        int offset = 0;

        while (offset + 4 <= extraField.Length)
        {
            ushort headerId = BinaryPrimitives.ReadUInt16LittleEndian(extraField[offset..]);
            ushort dataSize = BinaryPrimitives.ReadUInt16LittleEndian(extraField[(offset + 2)..]);
            offset += 4;

            if (headerId == ZipConstants.Zip64ExtraFieldHeaderId)
            {
                int dataOffset = offset;

                // Fields appear in order, only if corresponding 32-bit field was 0xFFFFFFFF
                if (entry.UncompressedSize == ZipConstants.Zip64Marker32 && dataOffset + 8 <= offset + dataSize)
                {
                    entry = entry with
                    {
                        UncompressedSize = BinaryPrimitives.ReadInt64LittleEndian(extraField[dataOffset..])
                    };
                    dataOffset += 8;
                }

                if (entry.CompressedSize == ZipConstants.Zip64Marker32 && dataOffset + 8 <= offset + dataSize)
                {
                    entry = entry with
                    {
                        CompressedSize = BinaryPrimitives.ReadInt64LittleEndian(extraField[dataOffset..])
                    };
                    dataOffset += 8;
                }

                if (entry.LocalHeaderOffset == ZipConstants.Zip64Marker32 && dataOffset + 8 <= offset + dataSize)
                {
                    entry = entry with
                    {
                        LocalHeaderOffset = BinaryPrimitives.ReadInt64LittleEndian(extraField[dataOffset..])
                    };
                }

                break; // Found ZIP64 info, done
            }

            offset += dataSize;
        }

        return entry;
    }

    #endregion

    #region Local Header and Extraction

    /// <inheritdoc />
    public async Task<ZipLocalHeader> ReadLocalHeaderAsync(
        long localHeaderOffset,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (localHeaderOffset < 0 || localHeaderOffset >= _stream.Length)
        {
            throw new InvalidOffsetException(localHeaderOffset, _stream.Length, "Local Header", FilePath);
        }

        _stream.Seek(localHeaderOffset, SeekOrigin.Begin);

        byte[] buffer = new byte[ZipConstants.LocalFileHeaderFixedSize];
        await _stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

        // Validate signature
        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (signature != ZipConstants.LocalFileHeaderSignature)
        {
            throw new InvalidSignatureException(
                ZipConstants.LocalFileHeaderSignature,
                signature,
                "Local File Header",
                FilePath,
                localHeaderOffset);
        }

        return new ZipLocalHeader
        {
            GeneralPurposeBitFlag = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6)),
            CompressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(8)),
            Crc32 = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(14)),
            CompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(18)),
            UncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(22)),
            FileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(26)),
            ExtraFieldLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28))
        };
    }

    /// <inheritdoc />
    public async Task<Stream> OpenEntryStreamAsync(
        ZipEntryInfo entry,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long startTimestamp = Stopwatch.GetTimestamp();

        // Check encryption
        if (entry.IsEncrypted)
        {
            throw new EncryptedEntryException("(entry)", FilePath);
        }

        // Check compression method
        if (entry.CompressionMethod != ZipConstants.CompressionMethodStore &&
            entry.CompressionMethod != ZipConstants.CompressionMethodDeflate)
        {
            throw new UnsupportedCompressionException(entry.CompressionMethod, "(entry)", FilePath);
        }

        // Read local header to get variable field lengths
        ZipLocalHeader localHeader = await ReadLocalHeaderAsync(entry.LocalHeaderOffset, cancellationToken)
            .ConfigureAwait(false);

        // Calculate data start position
        long dataStart = entry.LocalHeaderOffset + localHeader.TotalHeaderSize;

        // Seek to data start
        _stream.Seek(dataStart, SeekOrigin.Begin);

        // Create bounded sub-stream
        SubStream dataStream = new SubStream(_stream, entry.CompressedSize, leaveOpen: true);

        string compressionTag = entry.CompressionMethod == ZipConstants.CompressionMethodStore ? "store" : "deflate";
        string sizeBucket = ClassifySizeBucket(entry.UncompressedSize);

        double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        ZipTelemetry.ExtractionDuration.Record(elapsedMs,
            new KeyValuePair<string, object?>("size_bucket", sizeBucket),
            new KeyValuePair<string, object?>("compression", compressionTag));

        ZipTelemetry.BytesExtracted.Add(entry.UncompressedSize,
            new KeyValuePair<string, object?>("compression", compressionTag));

        // Wrap in decompression stream if needed
        return entry.CompressionMethod switch
        {
            ZipConstants.CompressionMethodStore => dataStream,
            ZipConstants.CompressionMethodDeflate => new DeflateStream(dataStream, CompressionMode.Decompress, leaveOpen: false),
            _ => throw new UnsupportedCompressionException(entry.CompressionMethod, "(entry)", FilePath)
        };
    }

    private static string ClassifySizeBucket(long sizeBytes) => sizeBytes switch
    {
        < 1_024L => "tiny",
        < 1_048_576L => "small",
        < 10_485_760L => "medium",
        < 52_428_800L => "large",
        < 524_288_000L => "xlarge",
        _ => "huge"
    };

    #endregion

    #region IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            if (!_leaveOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            _disposed = true;
        }
    }

    #endregion
}
