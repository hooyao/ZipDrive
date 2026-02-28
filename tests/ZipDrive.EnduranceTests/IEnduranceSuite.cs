using Xunit.Abstractions;

namespace ZipDrive.EnduranceTests;

/// <summary>
/// Interface for a virtual endurance test suite.
/// Each suite owns a group of tasks and tracks results independently.
/// </summary>
public interface IEnduranceSuite
{
    string Name { get; }
    int TaskCount { get; }
    Task RunAsync(CancellationToken ct);
    SuiteResult GetResult();
    void PrintReport(ITestOutputHelper output);
}

/// <summary>
/// Results collected by a single endurance suite.
/// </summary>
public sealed class SuiteResult
{
    public List<string> Errors { get; } = new();
    public long TotalOperations;
    public long VerifiedOperations;
}
