namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Classifies file sizes into metric tag buckets.
/// </summary>
internal static class SizeBucketClassifier
{
    private const long OneKb = 1_024L;
    private const long OneMb = 1_048_576L;
    private const long TenMb = 10_485_760L;
    private const long FiftyMb = 52_428_800L;
    private const long FiveHundredMb = 524_288_000L;

    /// <summary>
    /// Returns a size bucket string for the given byte count.
    /// </summary>
    internal static string Classify(long sizeBytes) => sizeBytes switch
    {
        < OneKb => "tiny",
        < OneMb => "small",
        < TenMb => "medium",
        < FiftyMb => "large",
        < FiveHundredMb => "xlarge",
        _ => "huge"
    };
}
