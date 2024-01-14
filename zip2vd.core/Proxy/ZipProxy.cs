using System.IO.Compression;
using System.Text;
using DokanNet;
using Microsoft.Extensions.Logging;

namespace zip2vd.core.Proxy;

public class ZipProxy : IDisposable
{
    private bool _disposed = false;
    private readonly object _lock = new object();

    private readonly ILogger<ZipProxy> _logger;
    private readonly ZipArchive _zipFile;

    public ZipProxy(string zipFilePath, ILogger<ZipProxy> logger, Encoding? encoding = null)
    {
        this._logger = logger;
        Encoding currentEncoding = encoding ?? Encoding.GetEncoding(0); // use system default encoding
        this._zipFile = ZipFile.Open(zipFilePath, ZipArchiveMode.Read, currentEncoding);
    }

    public EntryNode<ZipEntryAttr> BuildNodeTree(EntryNode<ZipEntryAttr> root)
    {
        if (this._disposed)
        {
            throw new ObjectDisposedException(nameof(ZipProxy));
        }

        foreach (ZipArchiveEntry entry in this._zipFile.Entries)
        {
            EntryNode<ZipEntryAttr> currentNode = root;
            string[] parts = entry.ParsePath();
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (!string.IsNullOrEmpty(part))
                {
                    if (i < parts.Length - 1)
                    {
                        // not last part
                        if (!currentNode.ChildNodes.ContainsKey(part))
                        {
                            FileInformation fileInfo = new FileInformation()
                            {
                                Attributes = FileAttributes.Directory | FileAttributes.ReadOnly,
                                CreationTime = entry.LastWriteTime.UtcDateTime,
                                FileName = part,
                                LastAccessTime = entry.LastWriteTime.UtcDateTime,
                                LastWriteTime = entry.LastWriteTime.UtcDateTime,
                                Length = 0L
                            };
                            EntryNode<ZipEntryAttr> childNode =
                                new EntryNode<ZipEntryAttr>(true, part, currentNode, fileInfo);
                            currentNode.AddChild(childNode);
                            currentNode = childNode;
                        }
                        else
                        {
                            currentNode = currentNode.ChildNodes[part];
                        }
                    }
                    else
                    {
                        if (!currentNode.ChildNodes.ContainsKey(part))
                        {
                            // last part
                            FileInformation fileInfo = new FileInformation()
                            {
                                Attributes = (entry.IsDirectory() ? FileAttributes.Directory : FileAttributes.Normal) |
                                             FileAttributes.ReadOnly,
                                CreationTime = entry.LastWriteTime.UtcDateTime,
                                FileName = part,
                                LastAccessTime = entry.LastWriteTime.UtcDateTime,
                                LastWriteTime = entry.LastWriteTime.UtcDateTime,
                                Length = entry.Length
                            };
                            ZipEntryAttr zipEntryAttr = new ZipEntryAttr(entry.FullName);
                            EntryNode<ZipEntryAttr> childNode =
                                new EntryNode<ZipEntryAttr>(entry.IsDirectory(), part, currentNode, fileInfo,
                                    zipEntryAttr);
                            currentNode.AddChild(childNode);
                            currentNode = childNode;
                        }
                        else
                        {
                            currentNode = currentNode.ChildNodes[part];
                        }
                    }
                }
            }
        }

        return root;
    }

    private EntryNode<ZipEntryAttr> LocateNode(EntryNode<ZipEntryAttr> root, string[] parts)
    {
        if (parts.Length == 0) //root
        {
            return root;
        }

        EntryNode<ZipEntryAttr> currentNode = root;
        foreach (string part in parts)
        {
            if (!string.IsNullOrEmpty(part))
            {
                if (currentNode.ChildNodes.ContainsKey(part))
                {
                    currentNode = currentNode.ChildNodes[part];
                }
                else
                {
                    throw new FileNotFoundException();
                }
            }
        }

        return currentNode;
    }

    public void Dispose()
    {
        lock (this._lock)
        {
            if (this._disposed)
                return;

            this._zipFile.Dispose();

            this._disposed = true;
        }
    }
}