namespace ZipDriveV3.Domain.Exceptions;

public class ArchiveNotFoundException : Exception
{
    public string FilePath { get; }

    public ArchiveNotFoundException(string filePath)
        : base($"Archive not found: {filePath}")
    {
        FilePath = filePath;
    }

    public ArchiveNotFoundException(string filePath, Exception innerException)
        : base($"Archive not found: {filePath}", innerException)
    {
        FilePath = filePath;
    }
}
