using System.Text;

namespace ZipDrive.EnduranceTests;

/// <summary>
/// Rich error context captured on first failure during endurance testing.
/// Contains enough information to reproduce and debug without re-running.
/// </summary>
public sealed class EnduranceFailure
{
    public required string Suite { get; init; }
    public required int TaskId { get; init; }
    public required string Workload { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public string? FilePath { get; init; }
    public string? Operation { get; init; }
    public string? ExpectedHash { get; init; }
    public string? ActualHash { get; init; }
    public string? SampleDescription { get; init; }
    public Exception? Exception { get; init; }
    public int CacheMemoryEntries { get; init; }
    public int CacheDiskEntries { get; init; }
    public int CacheBorrowedCount { get; init; }
    public int CachePendingCleanup { get; init; }

    public string FormatDiagnostic()
    {
        StringBuilder sb = new();
        sb.AppendLine();
        sb.AppendLine("══════════════════════════════════════════════════════════════");
        sb.AppendLine("  ENDURANCE TEST FAILED — First Error");
        sb.AppendLine("══════════════════════════════════════════════════════════════");
        sb.AppendLine();
        sb.AppendLine($"  Suite:     {Suite}");
        sb.AppendLine($"  Task:      #{TaskId} ({Workload})");
        sb.AppendLine($"  Elapsed:   {Elapsed:hh\\:mm\\:ss\\.fff} into run");
        sb.AppendLine();

        if (FilePath != null)
            sb.AppendLine($"  File:      {FilePath}");
        if (Operation != null)
            sb.AppendLine($"  Operation: {Operation}");

        sb.AppendLine();

        if (ExpectedHash != null && ActualHash != null)
        {
            sb.AppendLine("  ERROR: SHA-256 mismatch");
            sb.AppendLine($"    Expected: {ExpectedHash}");
            sb.AppendLine($"    Actual:   {ActualHash}");
            if (SampleDescription != null)
                sb.AppendLine($"    Sample:   {SampleDescription}");
        }
        else if (Exception != null)
        {
            sb.AppendLine($"  ERROR: {Exception.GetType().Name}: {Exception.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("  Cache State at Failure:");
        sb.AppendLine($"    Memory entries: {CacheMemoryEntries}, Disk entries: {CacheDiskEntries}");
        sb.AppendLine($"    Borrowed handles: {CacheBorrowedCount}, Pending cleanup: {CachePendingCleanup}");

        if (Exception != null)
        {
            sb.AppendLine();
            sb.AppendLine("  Stack Trace:");
            foreach (string line in (Exception.StackTrace ?? "").Split('\n'))
                sb.AppendLine($"    {line.TrimEnd()}");
        }

        sb.AppendLine();
        sb.AppendLine("══════════════════════════════════════════════════════════════");
        return sb.ToString();
    }
}
