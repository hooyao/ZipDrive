using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using zip2vd.core.Cache;

namespace zip2vd.core;

public class ZipArchivePooledObjectPolicy : IPooledObjectPolicy<VerboseZipArchive>
{
    private readonly string _filePath;
    private readonly Encoding _encoding;
    private readonly ILoggerFactory _loggerFactory;

    public ZipArchivePooledObjectPolicy(string filePath, Encoding encoding, ILoggerFactory loggerFactory)
    {
        _filePath = filePath;
        _encoding = encoding;
        this._loggerFactory = loggerFactory;
    }

    public VerboseZipArchive Create()
    {
        return new VerboseZipArchive(this._filePath, this._encoding, this._loggerFactory.CreateLogger<VerboseZipArchive>());
    }

    public bool Return(VerboseZipArchive obj)
    {
        return true;
    }
}