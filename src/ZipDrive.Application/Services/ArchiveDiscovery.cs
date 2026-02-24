using Microsoft.Extensions.Logging;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Application.Services;

/// <summary>
/// Discovers ZIP archives in a directory tree up to a configurable depth.
/// </summary>
public sealed class ArchiveDiscovery : IArchiveDiscovery
{
    private const int MinDepth = 1;
    private const int MaxDepth = 6;

    private readonly ILogger<ArchiveDiscovery> _logger;

    public ArchiveDiscovery(ILogger<ArchiveDiscovery> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ArchiveDescriptor>> DiscoverAsync(
        string rootPath,
        int maxDepth = MaxDepth,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Root directory not found: {rootPath}");
        }

        // Clamp depth to valid range
        int clampedDepth = Math.Clamp(maxDepth, MinDepth, MaxDepth);

        _logger.LogInformation(
            "Discovering ZIP files under {RootPath} with depth {Depth}",
            rootPath, clampedDepth);

        List<ArchiveDescriptor> results = new();
        ScanDirectory(rootPath, rootPath, clampedDepth, 0, results, cancellationToken);

        _logger.LogInformation("Discovered {Count} ZIP files", results.Count);

        return Task.FromResult<IReadOnlyList<ArchiveDescriptor>>(results);
    }

    private void ScanDirectory(
        string rootPath,
        string currentPath,
        int maxDepth,
        int currentDepth,
        List<ArchiveDescriptor> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (currentDepth >= maxDepth)
            return;

        try
        {
            // Find ZIP files in current directory
            foreach (string filePath in Directory.EnumerateFiles(currentPath, "*.zip"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    FileInfo fileInfo = new(filePath);
                    string relativePath = Path.GetRelativePath(rootPath, filePath);
                    // Normalize to forward slashes
                    string virtualPath = relativePath.Replace('\\', '/');

                    results.Add(new ArchiveDescriptor
                    {
                        VirtualPath = virtualPath,
                        PhysicalPath = filePath,
                        SizeBytes = fileInfo.Length,
                        LastModifiedUtc = fileInfo.LastWriteTimeUtc
                    });
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    _logger.LogWarning(ex, "Skipping inaccessible ZIP file: {Path}", filePath);
                }
            }

            // Recurse into subdirectories
            foreach (string dirPath in Directory.EnumerateDirectories(currentPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    ScanDirectory(rootPath, dirPath, maxDepth, currentDepth + 1, results, cancellationToken);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    _logger.LogWarning(ex, "Skipping inaccessible directory: {Path}", dirPath);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Cannot access directory: {Path}", currentPath);
        }
    }
}
