using SharpCompress.Archives.Rar;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.Archives.Rar;

/// <summary>
/// RAR-specific entry extractor. Opens the archive with SharpCompress,
/// locates the requested entry, decompresses it to a MemoryStream, and returns it.
///
/// The IRarArchive instance is disposed via OnDisposed callback after
/// the stream has been consumed by the cache storage strategy.
/// </summary>
public sealed class RarEntryExtractor : IArchiveEntryExtractor
{
    public string FormatId => "rar";

    public async Task<ExtractionResult> ExtractAsync(
        string archiveKey, string archivePath, string internalPath, CancellationToken cancellationToken = default)
    {
        // Synthetic warning file for solid archives -- serve static content, no archive I/O
        if (internalPath == RarStructureBuilder.UnsupportedWarningFileName)
        {
            return new ExtractionResult
            {
                Stream = new MemoryStream(RarStructureBuilder.SolidWarningContent, writable: false),
                SizeBytes = RarStructureBuilder.SolidWarningContent.Length,
            };
        }

        var archive = RarArchive.OpenArchive(archivePath);
        try
        {
            string normalizedTarget = RarStructureBuilder.NormalizePath(internalPath);
            var entry = archive.Entries
                .FirstOrDefault(e => !e.IsDirectory &&
                    RarStructureBuilder.NormalizePath(e.Key ?? "") == normalizedTarget)
                ?? throw new FileNotFoundException($"Entry not found in RAR: {internalPath}");

            if (entry.Size > int.MaxValue)
                throw new NotSupportedException(
                    $"RAR entry too large for memory extraction: {entry.Size} bytes. " +
                    "Files larger than 2GB are not supported in the RAR provider.");

            var ms = new MemoryStream((int)entry.Size);
            using (var entryStream = entry.OpenEntryStream())
            {
                await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            }
            ms.Position = 0;

            return new ExtractionResult
            {
                Stream = ms,
                SizeBytes = entry.Size,
                OnDisposed = () =>
                {
                    archive.Dispose();
                    return ValueTask.CompletedTask;
                }
            };
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }
}
