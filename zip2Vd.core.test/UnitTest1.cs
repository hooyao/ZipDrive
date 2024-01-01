using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using DokanNet;
using Xunit.Abstractions;
using zip2vd.core;
using zip2vd.core.Common;

namespace zip2Vd.core.test;

public class UnitTest1
{
    private readonly ITestOutputHelper _testOutputHelper;
    public UnitTest1(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }
    [Fact]
    public void Test1()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string filePath = @"D:\test1.zip";

        Encoding ansiEncoding = Encoding.GetEncoding(0);
        using (ZipArchive zipFile = ZipFile.Open(filePath, ZipArchiveMode.Read, ansiEncoding))
        {
            EntryNode<ZipEntryAttr> root = new EntryNode<ZipEntryAttr>(true, "/", null, new FileInformation(), new ZipEntryAttr());
            foreach (ZipArchiveEntry entry in zipFile.Entries)
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
                                    Attributes = FileAttributes.Directory,
                                    CreationTime = entry.LastWriteTime.UtcDateTime,
                                    FileName = part,
                                    LastAccessTime = entry.LastWriteTime.UtcDateTime,
                                    LastWriteTime = entry.LastWriteTime.UtcDateTime,
                                    Length = 0L
                                };
                                EntryNode<ZipEntryAttr> childNode = new EntryNode<ZipEntryAttr>(true, part, currentNode, fileInfo);
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
                                    Attributes = entry.IsDirectory() ? FileAttributes.Directory : FileAttributes.Normal,
                                    CreationTime = entry.LastWriteTime.UtcDateTime,
                                    FileName = part,
                                    LastAccessTime = entry.LastWriteTime.UtcDateTime,
                                    LastWriteTime = entry.LastWriteTime.UtcDateTime,
                                    Length = entry.Length
                                };
                                EntryNode<ZipEntryAttr> childNode =
                                    new EntryNode<ZipEntryAttr>(entry.IsDirectory(), part, currentNode, fileInfo);
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

            PrintTree(root, 0);
        }
    }

    public void PrintTree(EntryNode<ZipEntryAttr> node, int depth)
    {
        Console.WriteLine(new string('\t', depth) + node.Name);
        foreach (EntryNode<ZipEntryAttr> child in node.ChildNodes.Values)
        {
            PrintTree(child, depth + 1);
        }
    }
}