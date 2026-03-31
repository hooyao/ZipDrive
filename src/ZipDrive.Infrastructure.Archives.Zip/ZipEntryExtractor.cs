using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// ZIP-specific entry extractor. Resolves ZipEntryInfo from ZipFormatMetadataStore,
/// creates a fresh IZipReader per extraction, and returns a decompressed stream.
/// </summary>
public sealed class ZipEntryExtractor : IArchiveEntryExtractor
{
    private readonly IZipReaderFactory _readerFactory;
    private readonly ZipFormatMetadataStore _metadataStore;

    public string FormatId => "zip";

    public ZipEntryExtractor(
        IZipReaderFactory readerFactory,
        ZipFormatMetadataStore metadataStore)
    {
        _readerFactory = readerFactory;
        _metadataStore = metadataStore;
    }

    public async Task<ExtractionResult> ExtractAsync(
        string archivePath,
        string internalPath,
        CancellationToken cancellationToken = default)
    {
        ZipEntryInfo zipEntry = _metadataStore.Get(archivePath, internalPath);
        IZipReader reader = _readerFactory.Create(archivePath);
        try
        {
            Stream decompressedStream = await reader.OpenEntryStreamAsync(zipEntry, cancellationToken)
                .ConfigureAwait(false);

            return new ExtractionResult
            {
                Stream = decompressedStream,
                SizeBytes = zipEntry.UncompressedSize,
                OnDisposed = async () => await reader.DisposeAsync().ConfigureAwait(false)
            };
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
