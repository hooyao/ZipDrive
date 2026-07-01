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

    private readonly IFormatRegistry _formatRegistry;
    private readonly ILogger<ArchiveDiscovery> _logger;

    public ArchiveDiscovery(IFormatRegistry formatRegistry, ILogger<ArchiveDiscovery> logger)
    {
        _formatRegistry = formatRegistry;
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
            "Discovering archive files under {RootPath} with depth {Depth}",
            rootPath, clampedDepth);

        List<ArchiveDescriptor> results = new();
        ScanDirectory(rootPath, rootPath, clampedDepth, 0, results, cancellationToken);

        _logger.LogInformation("Discovered {Count} archive files", results.Count);

        return Task.FromResult<IReadOnlyList<ArchiveDescriptor>>(results);
    }

    /// <inheritdoc />
    public ArchiveDescriptor? DescribeFile(string rootPath, string filePath)
    {
        try
        {
            string? formatId = _formatRegistry.DetectFormat(filePath);
            if (formatId == null)
                return null;

            FileInfo fileInfo = new(filePath);
            if (!fileInfo.Exists)
                return null;

            string virtualPath = ArchivePathHelper.ToVirtualPath(rootPath, filePath);

            return new ArchiveDescriptor
            {
                VirtualPath = virtualPath,
                PhysicalPath = Path.GetFullPath(filePath),
                SizeBytes = fileInfo.Length,
                LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                FormatId = formatId
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Cannot access archive file for descriptor: {Path}", filePath);
            return null;
        }
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
            // Find archive files in current directory (all registered formats)
            foreach (string ext in _formatRegistry.SupportedExtensions)
            {
                foreach (string filePath in Directory.EnumerateFiles(currentPath, $"*{ext}"))
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
                            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                            FormatId = _formatRegistry.DetectFormat(filePath) ?? "zip"
                        });
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        _logger.LogWarning(ex, "Skipping inaccessible archive file: {Path}", filePath);
                    }
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
