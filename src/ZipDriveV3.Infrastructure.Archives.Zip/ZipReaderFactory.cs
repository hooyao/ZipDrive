using Microsoft.Extensions.Logging;

namespace ZipDriveV3.Infrastructure.Archives.Zip;

/// <summary>
/// Creates <see cref="ZipReader"/> instances that manage their own FileStream
/// (81920-byte buffer, async I/O, FileShare.Read).
/// </summary>
public sealed class ZipReaderFactory : IZipReaderFactory
{
    private readonly ILoggerFactory? _loggerFactory;

    public ZipReaderFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public IZipReader Create(string filePath)
    {
        var logger = _loggerFactory?.CreateLogger<ZipReader>();
        return new ZipReader(filePath, logger);
    }
}
