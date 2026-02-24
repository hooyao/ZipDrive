namespace ZipDriveV3.TestHelpers;

/// <summary>
/// Predefined ZIP generation profiles for test coverage.
/// Each profile targets specific caching paths and edge cases.
/// </summary>
public enum ZipProfile
{
    /// <summary>50 files, 1KB-10KB each. All memory tier (&lt; 50MB cutoff).</summary>
    TinyFiles,

    /// <summary>100 files, 100KB-5MB each. Memory tier.</summary>
    SmallFiles,

    /// <summary>80 small + 10 medium (20-49MB) + 10 large (50-200MB). Both tiers.</summary>
    MixedFiles,

    /// <summary>20 files, 50-500MB each. All disk tier (&gt;= 50MB cutoff).</summary>
    LargeFiles,

    /// <summary>200 files in deeply nested directories (10+ levels).</summary>
    DeepNesting,

    /// <summary>500 files all in root directory.</summary>
    FlatStructure,

    /// <summary>5 large sequential files (100-500MB). Simulates video content.</summary>
    VideoSimulation,

    /// <summary>Edge cases: unicode names, empty files, single-byte, cutoff boundary.</summary>
    EdgeCases,

    /// <summary>
    /// 15 small files (100KB-4MB) + 3 medium files (5-6MB).
    /// Designed for endurance tests with SmallFileCutoffMb=5 to exercise both cache tiers.
    /// </summary>
    EnduranceMixed
}
