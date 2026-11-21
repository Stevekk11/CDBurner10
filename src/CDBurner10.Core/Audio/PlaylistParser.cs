using System.Text;
using System.Xml;
using CDBurner10.Core.Audio.Cue;
using CDBurner10.Core.Utilities;

namespace CDBurner10.Core.Audio;

public static class PlaylistParser
{
    public static bool IsCue(string filePath) =>
        Path.GetExtension(filePath).Equals(".cue", StringComparison.OrdinalIgnoreCase);

    public static bool IsM3u(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWpl(string filePath) =>
        Path.GetExtension(filePath).Equals(".wpl", StringComparison.OrdinalIgnoreCase);

    public static bool IsNra(string filePath) =>
        Path.GetExtension(filePath).Equals(".nra", StringComparison.OrdinalIgnoreCase);

    public static bool IsSupportedPlaylist(string filePath) =>
        IsM3u(filePath) || IsWpl(filePath) || IsNra(filePath) || IsCue(filePath);

    public static string[] GetFilesFromPlaylist(string filePath)
    {
        if (IsM3u(filePath)) return GetFilesFromM3u(filePath);
        if (IsWpl(filePath)) return GetFilesFromWpl(filePath);
        if (IsNra(filePath)) return GetFilesFromNra(filePath);
        if (IsCue(filePath)) return GetFilesFromCue(filePath);
        return [];
    }

    public static string[] GetFilesFromCue(string filePath)
    {
        try
        {
            var cue = CueSheet.LoadFromFile(filePath);
            return cue.Tracks
                .Select(t => t.DataFileName)
                .Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static string[] GetFilesFromM3u(string filePath)
    {
        var list = new List<string>();
        try
        {
            var encoding = Path.GetExtension(filePath).Equals(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8
                : Encoding.Default;

            using var reader = new StreamReader(filePath, encoding, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    continue;

                var fullPath = PathHelper.ResolveRelativePath(filePath, line);
                if (BassAudioEngine.IsAudioFile(fullPath) && File.Exists(fullPath))
                {
                    list.Add(fullPath);
                }
            }
        }
        catch
        {
            // Ignore IO errors
        }
        return list.ToArray();
    }

    public static string[] GetFilesFromWpl(string filePath)
    {
        var list = new List<string>();
        try
        {
            var doc = new XmlDocument();
            doc.Load(filePath);
            var mediaNodes = doc.GetElementsByTagName("media");
            foreach (XmlNode node in mediaNodes)
            {
                var src = node.Attributes?["src"]?.Value;
                if (string.IsNullOrWhiteSpace(src)) continue;
                var fullPath = PathHelper.ResolveRelativePath(filePath, src);
                if (BassAudioEngine.IsAudioFile(fullPath) && File.Exists(fullPath))
                {
                    list.Add(fullPath);
                }
            }
        }
        catch
        {
            // Ignore malformed WPL
        }
        return list.ToArray();
    }

    public static string[] GetFilesFromNra(string filePath)
    {
        var list = new List<string>();
        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            for (int i = 0; i + 4 < bytes.Length; i++)
            {
                // UTF-16LE drive path pattern: X\0:\0\\
                if (bytes[i] >= 'A' && bytes[i] <= 'z' && bytes[i + 1] == 0 && bytes[i + 2] == ':' && bytes[i + 4] == '\\')
                {
                    int end = i;
                    while (end < bytes.Length - 1 && (bytes[end] != 0 || bytes[end + 1] != 0))
                    {
                        end += 2;
                    }
                    var path = Encoding.Unicode.GetString(bytes, i, end - i).TrimEnd('\0');
                    if (list.Count == 0 || list[^1] != path)
                    {
                        list.Add(path);
                    }
                }
                // ASCII drive path pattern: X:\
                else if (bytes[i] >= 'A' && bytes[i] <= 'z' && bytes[i + 1] == ':' && bytes[i + 2] == '\\')
                {
                    int end = i;
                    while (end < bytes.Length - 1 && bytes[end + 1] != 0)
                    {
                        end++;
                    }
                    var path = Encoding.ASCII.GetString(bytes, i, end - i + 1).TrimEnd('\0');
                    if (list.Count == 0 || list[^1] != path)
                    {
                        list.Add(path);
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed NRA
        }
        return list.ToArray();
    }

    public static bool IsCda(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        if (!Path.GetExtension(filePath).Equals(".cda", StringComparison.OrdinalIgnoreCase))
            return false;
        if (filePath.StartsWith("MixedMode:", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            byte[] header = new byte[24];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Read(header, 0, 24) < 24) return false;
            string cdda = Encoding.ASCII.GetString(header, 8, 4);
            string fmt = Encoding.ASCII.GetString(header, 12, 3);
            return header[20] == 1 && cdda == "CDDA" && fmt == "fmt";
        }
        catch
        {
            return false;
        }
    }
}
