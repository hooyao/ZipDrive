namespace ZipDrive.Domain;

/// <summary>
/// Case-insensitive character comparer for use with KTrie on Windows.
/// </summary>
public sealed class CaseInsensitiveCharComparer : IEqualityComparer<char>
{
    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static readonly CaseInsensitiveCharComparer Instance = new();

    private CaseInsensitiveCharComparer() { }

    public bool Equals(char x, char y) =>
        char.ToUpperInvariant(x) == char.ToUpperInvariant(y);

    public int GetHashCode(char c) =>
        char.ToUpperInvariant(c).GetHashCode();
}
