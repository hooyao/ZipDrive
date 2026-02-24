namespace ZipDrive.Domain.Models;

public class ArchivesChangedEventArgs : EventArgs
{
    public required string ArchiveKey { get; init; }
    public required ArchiveChangeType ChangeType { get; init; }
}

public enum ArchiveChangeType
{
    Added,
    Removed
}
