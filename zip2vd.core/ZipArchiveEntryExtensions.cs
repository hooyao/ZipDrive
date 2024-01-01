using System.IO.Compression;
using System.Reflection;

namespace zip2vd.core;

public static class ZipArchiveEntryExtensions
{
    public static bool IsDirectory(this ZipArchiveEntry entry)
    {
        return entry.Name == string.Empty;
    }

    private static readonly char[] WindowsSeparators = new char[] { '\\', '/', ':' };
    private static readonly char[] UnixSeparators = new char[] { '/' };

    public static string[] ParsePath(this ZipArchiveEntry entry)
    {
        Type type = entry.GetType();

        // Get the FieldInfo for the private field
        FieldInfo? fieldInfo = type.GetField("_versionMadeByPlatform", BindingFlags.NonPublic | BindingFlags.Instance);

        if (fieldInfo != null)
        {
            /*
             *     Windows = 0, Unix = 3,
             */
            byte value = (byte)fieldInfo.GetValue(entry)!;

            if (value == 3)
            {
                return entry.FullName.Split(UnixSeparators, StringSplitOptions.RemoveEmptyEntries);
            }
            else
            {
                return entry.FullName.Split(WindowsSeparators, StringSplitOptions.RemoveEmptyEntries);
            }
        }
        else
        {
            // Fallback to Unix
            return entry.FullName.Split('/');
        }
    }
}