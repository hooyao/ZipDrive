namespace ZipDrive.Domain.Exceptions;

/// <summary>
/// Base exception for all ZIP-related errors.
/// </summary>
public class ZipException : Exception
{
    /// <summary>
    /// Path to the ZIP file that caused the error, if available.
    /// </summary>
    public string? FilePath { get; }

    public ZipException(string message, string? filePath = null)
        : base(message)
    {
        FilePath = filePath;
    }

    public ZipException(string message, Exception innerException, string? filePath = null)
        : base(message, innerException)
    {
        FilePath = filePath;
    }
}

/// <summary>
/// Thrown when a ZIP file is malformed, truncated, or otherwise corrupt.
/// </summary>
public class CorruptZipException : ZipException
{
    /// <summary>
    /// Byte offset in the file where corruption was detected, if known.
    /// </summary>
    public long? CorruptionOffset { get; }

    public CorruptZipException(string message, string? filePath = null, long? offset = null)
        : base(message, filePath)
    {
        CorruptionOffset = offset;
    }

    public CorruptZipException(string message, Exception innerException, string? filePath = null, long? offset = null)
        : base(message, innerException, filePath)
    {
        CorruptionOffset = offset;
    }
}

/// <summary>
/// Thrown when a required signature is not found or is invalid.
/// </summary>
public class InvalidSignatureException : CorruptZipException
{
    /// <summary>
    /// The expected signature value.
    /// </summary>
    public uint ExpectedSignature { get; }

    /// <summary>
    /// The actual signature value found in the file.
    /// </summary>
    public uint ActualSignature { get; }

    public InvalidSignatureException(
        uint expected,
        uint actual,
        string context,
        string? filePath = null,
        long? offset = null)
        : base($"Invalid {context} signature: expected 0x{expected:X8}, found 0x{actual:X8}", filePath, offset)
    {
        ExpectedSignature = expected;
        ActualSignature = actual;
    }
}

/// <summary>
/// Thrown when End of Central Directory record cannot be found.
/// </summary>
public class EocdNotFoundException : CorruptZipException
{
    public EocdNotFoundException(string? filePath = null)
        : base("End of Central Directory (EOCD) signature not found. " +
               "The file may not be a valid ZIP archive or may be truncated.",
               filePath)
    {
    }
}

/// <summary>
/// Thrown when a compression method is not supported.
/// </summary>
public class UnsupportedCompressionException : ZipException
{
    /// <summary>
    /// The unsupported compression method value.
    /// </summary>
    public ushort CompressionMethod { get; }

    /// <summary>
    /// Path of the entry within the archive.
    /// </summary>
    public string EntryPath { get; }

    public UnsupportedCompressionException(ushort method, string entryPath, string? filePath = null)
        : base(BuildMessage(method, entryPath), filePath)
    {
        CompressionMethod = method;
        EntryPath = entryPath;
    }

    private static string BuildMessage(ushort method, string entryPath)
    {
        string methodName = method switch
        {
            9 => "Deflate64",
            12 => "BZIP2",
            14 => "LZMA",
            93 => "Zstandard",
            _ => $"method {method}"
        };

        return $"Unsupported compression {methodName} for entry '{entryPath}'. " +
               "Only Store (0) and Deflate (8) are supported.";
    }
}

/// <summary>
/// Thrown when attempting to extract an encrypted entry without password support.
/// </summary>
public class EncryptedEntryException : ZipException
{
    /// <summary>
    /// Path of the encrypted entry within the archive.
    /// </summary>
    public string EntryPath { get; }

    public EncryptedEntryException(string entryPath, string? filePath = null)
        : base($"Entry '{entryPath}' is encrypted. Password-protected archives are not supported.",
               filePath)
    {
        EntryPath = entryPath;
    }
}

/// <summary>
/// Thrown when the archive spans multiple disks, which is not supported.
/// </summary>
public class MultiDiskArchiveException : ZipException
{
    public MultiDiskArchiveException(string? filePath = null)
        : base("Multi-disk spanning archives are not supported.", filePath)
    {
    }
}

/// <summary>
/// Thrown when Central Directory entry count doesn't match EOCD declaration.
/// </summary>
public class EntryCountMismatchException : CorruptZipException
{
    /// <summary>
    /// Entry count declared in EOCD.
    /// </summary>
    public long ExpectedCount { get; }

    /// <summary>
    /// Actual number of entries parsed.
    /// </summary>
    public long ActualCount { get; }

    public EntryCountMismatchException(long expected, long actual, string? filePath = null)
        : base($"Entry count mismatch: EOCD declares {expected} entries, but {actual} were found.",
               filePath)
    {
        ExpectedCount = expected;
        ActualCount = actual;
    }
}

/// <summary>
/// Thrown when a file offset exceeds the file size.
/// </summary>
public class InvalidOffsetException : CorruptZipException
{
    /// <summary>
    /// The invalid offset value.
    /// </summary>
    public long Offset { get; }

    /// <summary>
    /// The file size.
    /// </summary>
    public long FileSize { get; }

    public InvalidOffsetException(long offset, long fileSize, string context, string? filePath = null)
        : base($"{context} offset ({offset}) exceeds file size ({fileSize}).", filePath, offset)
    {
        Offset = offset;
        FileSize = fileSize;
    }
}

/// <summary>
/// Thrown when a ZIP64 record is expected but not found.
/// </summary>
public class Zip64RequiredException : CorruptZipException
{
    public Zip64RequiredException(string context, string? filePath = null)
        : base($"ZIP64 {context} is required but was not found. " +
               "The archive may be corrupt or created by an incompatible tool.",
               filePath)
    {
    }
}
