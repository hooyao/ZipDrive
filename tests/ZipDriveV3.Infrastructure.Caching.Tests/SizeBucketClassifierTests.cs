using FluentAssertions;

namespace ZipDriveV3.Infrastructure.Caching.Tests;

public class SizeBucketClassifierTests
{
    [Theory]
    [InlineData(0, "tiny")]
    [InlineData(1, "tiny")]
    [InlineData(1_023, "tiny")]
    [InlineData(1_024, "small")]        // 1 KB boundary
    [InlineData(500_000, "small")]
    [InlineData(1_048_575, "small")]
    [InlineData(1_048_576, "medium")]   // 1 MB boundary
    [InlineData(5_000_000, "medium")]
    [InlineData(10_485_759, "medium")]
    [InlineData(10_485_760, "large")]   // 10 MB boundary
    [InlineData(30_000_000, "large")]
    [InlineData(52_428_799, "large")]
    [InlineData(52_428_800, "xlarge")]  // 50 MB boundary
    [InlineData(200_000_000, "xlarge")]
    [InlineData(524_287_999, "xlarge")]
    [InlineData(524_288_000, "huge")]   // 500 MB boundary
    [InlineData(1_000_000_000, "huge")]
    [InlineData(long.MaxValue, "huge")]
    public void Classify_ReturnsCorrectBucket(long sizeBytes, string expectedBucket)
    {
        SizeBucketClassifier.Classify(sizeBytes).Should().Be(expectedBucket);
    }
}
