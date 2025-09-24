using System;
using System.Threading.Tasks;
using Xunit;
using ZipDriveV2.Archives;

namespace ZipDriveV2.Tests;

public class StubProviderTests
{
    [Fact]
    public void FormatId_is_zip()
    {
        var p = new StubZipArchiveProvider();
        Assert.Equal("zip", p.FormatId);
    }

    [Theory]
    [InlineData("file.zip", true)]
    [InlineData("FILE.ZIP", true)]
    [InlineData("file.txt", false)]
    public void CanOpen_checks_extension(string name, bool expected)
    {
        var p = new StubZipArchiveProvider();
        var can = p.CanOpen(name, ReadOnlySpan<byte>.Empty);
        Assert.Equal(expected, can);
    }
}