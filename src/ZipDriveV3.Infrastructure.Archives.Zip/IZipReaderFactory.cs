namespace ZipDriveV3.Infrastructure.Archives.Zip;

/// <summary>
/// Factory for creating <see cref="IZipReader"/> instances from file paths.
/// </summary>
public interface IZipReaderFactory
{
    /// <summary>
    /// Creates a new <see cref="IZipReader"/> for the specified ZIP file.
    /// The returned reader owns its underlying stream and must be disposed.
    /// </summary>
    /// <param name="filePath">Absolute path to the ZIP file.</param>
    /// <returns>A new <see cref="IZipReader"/> instance.</returns>
    IZipReader Create(string filePath);
}
