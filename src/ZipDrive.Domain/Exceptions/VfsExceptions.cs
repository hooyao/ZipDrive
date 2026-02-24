namespace ZipDrive.Domain.Exceptions;

/// <summary>
/// Base exception for virtual file system errors.
/// </summary>
public class VfsException : Exception
{
    public VfsException(string message) : base(message) { }
    public VfsException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when a file is not found in the virtual file system.
/// </summary>
public class VfsFileNotFoundException : VfsException
{
    public string Path { get; }

    public VfsFileNotFoundException(string path)
        : base($"File not found: {path}")
    {
        Path = path;
    }

    public VfsFileNotFoundException(string path, Exception innerException)
        : base($"File not found: {path}", innerException)
    {
        Path = path;
    }
}

/// <summary>
/// Thrown when a directory is not found in the virtual file system.
/// </summary>
public class VfsDirectoryNotFoundException : VfsException
{
    public string Path { get; }

    public VfsDirectoryNotFoundException(string path)
        : base($"Directory not found: {path}")
    {
        Path = path;
    }
}

/// <summary>
/// Thrown when an operation is denied (e.g., reading a directory as a file).
/// </summary>
public class VfsAccessDeniedException : VfsException
{
    public string Path { get; }

    public VfsAccessDeniedException(string path)
        : base($"Access denied: {path}")
    {
        Path = path;
    }
}
