namespace ZipDriveV3.Domain.Abstractions;

/// <summary>
/// Pluggable archive format provider (ZIP, TAR, 7Z, etc.)
/// </summary>
public interface IArchiveProvider
{
    /// <summary>Format identifier (e.g., "zip")</summary>
    string FormatId { get; }

    /// <summary>Display name (e.g., "ZIP Archive")</summary>
    string DisplayName { get; }

    /// <summary>
    /// Determines if this provider can open the given file
    /// </summary>
    /// <param name="filePath">Full path to archive file</param>
    /// <param name="headerSample">First 256 bytes for magic number detection</param>
    bool CanOpen(string filePath, ReadOnlySpan<byte> headerSample);

    /// <summary>
    /// Opens an archive session
    /// </summary>
    Task<IArchiveSession> OpenAsync(string filePath, CancellationToken cancellationToken = default);
}
