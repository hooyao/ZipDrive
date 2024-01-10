namespace zip2vd.core.Configuration
{
    public class ArchiveFileSystemOptions
    {
        public int SmallFileCacheSizeInMb { get; set; } = 1024; // 1GB

        public string? LargeFileCacheDir { get; set; } = null;

        public int SmallFileSizeCutoffInMb { get; set; } = 100; // 100MB

        public int LargeFileCacheSizeInMb { get; set; } = 10240; // 10GB

        public int MaxReadConcurrency { get; set; } = 4;
    }
}