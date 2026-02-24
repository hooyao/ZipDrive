namespace ZipDrive.Domain.Exceptions;

public class InvalidArchiveException : Exception
{
    public string FilePath { get; }

    public InvalidArchiveException(string filePath, string message)
        : base($"Invalid archive '{filePath}': {message}")
    {
        FilePath = filePath;
    }

    public InvalidArchiveException(string filePath, string message, Exception innerException)
        : base($"Invalid archive '{filePath}': {message}", innerException)
    {
        FilePath = filePath;
    }
}
