namespace CDBurner10.Core.Utilities;

public static class PathHelper
{
    public static string ReplaceInvalidFileNameChars(string input, char replacement = '_')
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = input.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = replacement;
            }
        }
        return new string(chars);
    }

    public static string ResolveRelativePath(string baseFilePath, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return string.Empty;
        targetPath = targetPath.Trim().Trim('"');
        if (Path.IsPathRooted(targetPath))
        {
            return targetPath;
        }
        var baseDir = Path.GetDirectoryName(baseFilePath);
        return string.IsNullOrEmpty(baseDir)
            ? Path.GetFullPath(targetPath)
            : Path.GetFullPath(Path.Combine(baseDir, targetPath));
    }

    public static bool IsDriveInUnitMask(int unitMask, char driveLetter)
    {
        char upper = char.ToUpperInvariant(driveLetter);
        if (upper < 'A' || upper > 'Z') return false;
        int bit = 1 << (upper - 'A');
        return (unitMask & bit) != 0;
    }

    public static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }
}
