using System.Security.Cryptography;
using FluentAssertions;
using ZipDrive.TestHelpers;

namespace ZipDrive.EnduranceTests;

public class PartialSampleTests
{
    [Fact]
    public void TinyFile_GetsSingleSample_CoveringEntireFile()
    {
        byte[] content = TestZipGenerator.GenerateDeterministicContent("tiny.bin", 1000);
        var samples = TestZipGenerator.GeneratePartialSamples("tiny.bin", content);

        samples.Should().HaveCount(1);
        samples[0].Offset.Should().Be(0);
        samples[0].Length.Should().Be(1000);
        samples[0].FileName.Should().Be("tiny.bin");

        string expected = Convert.ToHexStringLower(SHA256.HashData(content));
        samples[0].Sha256.Should().Be(expected);
    }

    [Fact]
    public void SmallFile_Under1MB_GetsThreeSamples()
    {
        int size = 500 * 1024; // 500KB
        byte[] content = TestZipGenerator.GenerateDeterministicContent("small.bin", size);
        var samples = TestZipGenerator.GeneratePartialSamples("small.bin", content);

        samples.Should().HaveCount(3);

        // Sample 1: Start
        samples[0].Offset.Should().Be(0);
        samples[0].Length.Should().Be(65536);

        // Sample 2: Mid-file
        samples[1].Offset.Should().Be(size / 2);

        // Sample 3: Tail
        samples[2].Offset.Should().Be(size - 4096);
        samples[2].Length.Should().Be(4096);
    }

    [Fact]
    public void LargeFile_Over1MB_GetsSevenSamples()
    {
        int size = 5 * 1024 * 1024; // 5MB
        byte[] content = TestZipGenerator.GenerateDeterministicContent("large.bin", size);
        var samples = TestZipGenerator.GeneratePartialSamples("large.bin", content, chunkSize: 1024 * 1024);

        samples.Should().HaveCount(7);

        // Sample 1: Start
        samples[0].Offset.Should().Be(0);
        samples[0].Length.Should().Be(65536);

        // Sample 2: Chunk boundary cross (1MB - 32KB)
        samples[1].Offset.Should().Be(1024 * 1024 - 32768);
        samples[1].Length.Should().Be(65536);

        // Sample 3: Mid-file
        samples[2].Offset.Should().Be(size / 2);

        // Samples 4-5: Random (deterministic)
        samples[3].Length.Should().Be(65536);
        samples[4].Length.Should().Be(65536);

        // Sample 6: Near-end
        samples[5].Offset.Should().Be(size - 65536);

        // Sample 7: Tail
        samples[6].Offset.Should().Be(size - 4096);
        samples[6].Length.Should().Be(4096);
    }

    [Fact]
    public void AllSamples_HaveCorrectChecksums()
    {
        int size = 2 * 1024 * 1024; // 2MB
        byte[] content = TestZipGenerator.GenerateDeterministicContent("verify.bin", size);
        var samples = TestZipGenerator.GeneratePartialSamples("verify.bin", content);

        foreach (var sample in samples)
        {
            byte[] slice = content.AsSpan((int)sample.Offset, sample.Length).ToArray();
            string expected = Convert.ToHexStringLower(SHA256.HashData(slice));
            sample.Sha256.Should().Be(expected,
                $"sample at offset={sample.Offset} length={sample.Length}");
        }
    }

    [Fact]
    public void EmptyFile_GetsNoSamples()
    {
        var samples = TestZipGenerator.GeneratePartialSamples("empty.bin", []);
        samples.Should().BeEmpty();
    }

    [Fact]
    public void DeterministicSamples_AreReproducible()
    {
        byte[] content = TestZipGenerator.GenerateDeterministicContent("repro.bin", 3 * 1024 * 1024);
        var samples1 = TestZipGenerator.GeneratePartialSamples("repro.bin", content);
        var samples2 = TestZipGenerator.GeneratePartialSamples("repro.bin", content);

        samples1.Should().HaveCount(samples2.Count);
        for (int i = 0; i < samples1.Count; i++)
        {
            samples1[i].Offset.Should().Be(samples2[i].Offset);
            samples1[i].Length.Should().Be(samples2[i].Length);
            samples1[i].Sha256.Should().Be(samples2[i].Sha256);
        }
    }
}
