using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.ObjectPool;

namespace zip2vd.core;

public class ZipArchivePooledObjectPolicy : IPooledObjectPolicy<ZipArchive>
{
    private readonly string _filePath;
    private readonly Encoding _encoding;

    public ZipArchivePooledObjectPolicy(string filePath, Encoding encoding)
    {
        _filePath = filePath;
        _encoding = encoding;
    }

    public ZipArchive Create()
    {
        return ZipFile.Open(this._filePath, ZipArchiveMode.Read, this._encoding);
    }

    public bool Return(ZipArchive obj)
    {
        return true;
    }
}