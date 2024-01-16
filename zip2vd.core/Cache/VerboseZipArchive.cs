using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;

namespace zip2vd.core.Cache;

public class VerboseZipArchive : ZipArchive, IDisposable
{
    private readonly string _filePath;
    private readonly ILogger<VerboseZipArchive> _logger;

    public VerboseZipArchive(string filePath, Encoding encoding, ILogger<VerboseZipArchive> logger)
        : base(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0x1000, useAsync: false), ZipArchiveMode.Read, true, encoding)
    {
        this._filePath = filePath;
        this._logger = logger;
    }

    private VerboseZipArchive(Stream stream, ILogger<VerboseZipArchive> logger, string filePath) : base(stream)
    {
        this._logger = logger;
        this._filePath = filePath;
    }
    private VerboseZipArchive(Stream stream, ZipArchiveMode mode, ILogger<VerboseZipArchive> logger, string filePath) : base(stream, mode)
    {
        this._logger = logger;
        this._filePath = filePath;
    }
    private VerboseZipArchive(Stream stream, ZipArchiveMode mode, bool leaveOpen, ILogger<VerboseZipArchive> logger, string filePath) : base(stream, mode, leaveOpen)
    {
        this._logger = logger;
        this._filePath = filePath;
    }
    private VerboseZipArchive(Stream stream, ZipArchiveMode mode, bool leaveOpen, Encoding? entryNameEncoding, ILogger<VerboseZipArchive> logger, string filePath)
        : base(stream, mode, leaveOpen, entryNameEncoding)
    {
        this._logger = logger;
        this._filePath = filePath;
    }

    public new void Dispose()
    {
        this._logger.LogDebug("Disposing ZipArchive {ArchivePath}", this._filePath);
        base.Dispose();
    }
}