using System.Runtime.InteropServices;

namespace CDBurner10.Core.Utilities;

public sealed partial class StringLogicalComparer : IComparer<string>
{
    public static StringLogicalComparer Instance { get; } = new();

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string psz1, string psz2);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return StrCmpLogicalW(x, y);
    }
}
