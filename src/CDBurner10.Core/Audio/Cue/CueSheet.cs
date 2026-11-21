using System.Text;

namespace CDBurner10.Core.Audio.Cue;

public enum CueFileType
{
    WAVE,
    MP3,
    AIFF,
    BINARY,
    MOTOROLA
}

public enum CueDataType
{
    AUDIO,
    CDG,
    MODE1_2048,
    MODE1_2352,
    MODE2_2336,
    MODE2_2352,
    CDI_2336,
    CDI_2352
}

public readonly record struct CueIndex(int Number, int Minutes, int Seconds, int Frames)
{
    public TimeSpan ToTimeSpan() =>
        TimeSpan.FromMinutes(Minutes) + TimeSpan.FromSeconds(Seconds) + TimeSpan.FromSeconds(Frames / 75.0);

    public double TotalSeconds => Minutes * 60.0 + Seconds + Frames / 75.0;

    public static CueIndex Parse(int number, string mmssff)
    {
        var parts = mmssff.Trim().Split(':');
        int m = parts.Length > 0 && int.TryParse(parts[0], out var mv) ? mv : 0;
        int s = parts.Length > 1 && int.TryParse(parts[1], out var sv) ? sv : 0;
        int f = parts.Length > 2 && int.TryParse(parts[2], out var fv) ? fv : 0;
        return new CueIndex(number, m, s, f);
    }

    public string FormatMsf() => $"{Minutes:D2}:{Seconds:D2}:{Frames:D2}";
}

public sealed class CueTrack
{
    public int TrackNumber { get; set; }
    public CueDataType DataType { get; set; } = CueDataType.AUDIO;
    public string Title { get; set; } = string.Empty;
    public string Performer { get; set; } = string.Empty;
    public string Songwriter { get; set; } = string.Empty;
    public string Isrc { get; set; } = string.Empty;
    public string DataFileName { get; set; } = string.Empty;
    public CueFileType DataFileType { get; set; } = CueFileType.WAVE;
    public CueIndex? PreGap { get; set; }
    public CueIndex? PostGap { get; set; }
    public List<CueIndex> Indices { get; } = [];
    public List<string> Flags { get; } = [];
    public List<string> Comments { get; } = [];

    public CueIndex? GetIndex(int number)
    {
        foreach (var idx in Indices)
        {
            if (idx.Number == number) return idx;
        }
        return null;
    }

    public void AddIndex(int number, int minutes, int seconds, int frames)
    {
        Indices.Add(new CueIndex(number, minutes, seconds, frames));
    }
}

public sealed class CueSheet
{
    public string Title { get; set; } = string.Empty;
    public string Performer { get; set; } = string.Empty;
    public string Songwriter { get; set; } = string.Empty;
    public string Catalog { get; set; } = string.Empty;
    public string CdTextFile { get; set; } = string.Empty;
    public List<string> Comments { get; } = [];
    public List<CueTrack> Tracks { get; } = [];

    public CueSheet() { }

    public static CueSheet LoadFromFile(string filePath)
    {
        var content = File.ReadAllText(filePath, Encoding.UTF8);
        return Parse(content, Path.GetDirectoryName(filePath));
    }

    public static CueSheet Parse(string cueContent, string? baseDirectory = null)
    {
        var sheet = new CueSheet();
        string currentFile = string.Empty;
        CueFileType currentFileType = CueFileType.WAVE;
        CueTrack? currentTrack = null;

        using var reader = new StringReader(cueContent);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var firstSpace = line.IndexOf(' ');
            var command = (firstSpace > 0 ? line[..firstSpace] : line).ToUpperInvariant();
            var rest = firstSpace > 0 ? line[(firstSpace + 1)..].Trim() : string.Empty;

            switch (command)
            {
                case "REM":
                    if (currentTrack is null) sheet.Comments.Add(rest);
                    else currentTrack.Comments.Add(rest);
                    break;
                case "CATALOG":
                    sheet.Catalog = Unquote(rest);
                    break;
                case "CDTEXTFILE":
                    sheet.CdTextFile = Unquote(rest);
                    break;
                case "TITLE":
                    if (currentTrack is null) sheet.Title = Unquote(rest);
                    else currentTrack.Title = Unquote(rest);
                    break;
                case "PERFORMER":
                    if (currentTrack is null) sheet.Performer = Unquote(rest);
                    else currentTrack.Performer = Unquote(rest);
                    break;
                case "SONGWRITER":
                    if (currentTrack is null) sheet.Songwriter = Unquote(rest);
                    else currentTrack.Songwriter = Unquote(rest);
                    break;
                case "FILE":
                    ParseFileLine(rest, baseDirectory, out currentFile, out currentFileType);
                    break;
                case "TRACK":
                {
                    var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    int num = parts.Length > 0 && int.TryParse(parts[0], out var n) ? n : sheet.Tracks.Count + 1;
                    var dataType = CueDataType.AUDIO;
                    if (parts.Length > 1)
                    {
                        var dtStr = parts[1].Replace('/', '_');
                        Enum.TryParse(dtStr, ignoreCase: true, out dataType);
                    }
                    currentTrack = new CueTrack
                    {
                        TrackNumber = num,
                        DataType = dataType,
                        DataFileName = currentFile,
                        DataFileType = currentFileType
                    };
                    sheet.Tracks.Add(currentTrack);
                    break;
                }
                case "INDEX":
                    if (currentTrack is not null)
                    {
                        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && int.TryParse(parts[0], out var idxNum))
                        {
                            currentTrack.Indices.Add(CueIndex.Parse(idxNum, parts[1]));
                        }
                    }
                    break;
                case "PREGAP":
                    if (currentTrack is not null)
                        currentTrack.PreGap = CueIndex.Parse(0, rest);
                    break;
                case "POSTGAP":
                    if (currentTrack is not null)
                        currentTrack.PostGap = CueIndex.Parse(0, rest);
                    break;
                case "FLAGS":
                    if (currentTrack is not null)
                        currentTrack.Flags.AddRange(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "ISRC":
                    if (currentTrack is not null)
                        currentTrack.Isrc = Unquote(rest);
                    break;
            }
        }

        return sheet;
    }

    public CueTrack AddTrack(string title, string performer, string fileName, CueFileType fileType = CueFileType.WAVE)
    {
        var track = new CueTrack
        {
            TrackNumber = Tracks.Count + 1,
            Title = title,
            Performer = performer,
            DataFileName = fileName,
            DataFileType = fileType,
            DataType = CueDataType.AUDIO
        };
        Tracks.Add(track);
        return track;
    }

    public string ToCueString()
    {
        var sb = new StringBuilder();
        foreach (var rem in Comments) sb.AppendLine($"REM {rem}");
        if (!string.IsNullOrWhiteSpace(Catalog)) sb.AppendLine($"CATALOG {Catalog}");
        if (!string.IsNullOrWhiteSpace(Performer)) sb.AppendLine($"PERFORMER \"{EscapeQuotes(Performer)}\"");
        if (!string.IsNullOrWhiteSpace(Title)) sb.AppendLine($"TITLE \"{EscapeQuotes(Title)}\"");

        string? lastFile = null;
        foreach (var track in Tracks)
        {
            if (!string.Equals(lastFile, track.DataFileName, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"FILE \"{EscapeQuotes(track.DataFileName)}\" {track.DataFileType}");
                lastFile = track.DataFileName;
            }
            sb.AppendLine($"  TRACK {track.TrackNumber:D2} {track.DataType.ToString().Replace('_', '/')}");
            if (!string.IsNullOrWhiteSpace(track.Title)) sb.AppendLine($"    TITLE \"{EscapeQuotes(track.Title)}\"");
            if (!string.IsNullOrWhiteSpace(track.Performer)) sb.AppendLine($"    PERFORMER \"{EscapeQuotes(track.Performer)}\"");
            if (track.Flags.Count > 0) sb.AppendLine($"    FLAGS {string.Join(' ', track.Flags)}");
            if (track.PreGap.HasValue) sb.AppendLine($"    PREGAP {track.PreGap.Value.FormatMsf()}");
            foreach (var idx in track.Indices)
            {
                sb.AppendLine($"    INDEX {idx.Number:D2} {idx.FormatMsf()}");
            }
            if (track.PostGap.HasValue) sb.AppendLine($"    POSTGAP {track.PostGap.Value.FormatMsf()}");
        }

        return sb.ToString();
    }

    public void Save(string filePath, Encoding? encoding = null)
    {
        File.WriteAllText(filePath, ToCueString(), encoding ?? Encoding.UTF8);
    }

    private static void ParseFileLine(string rest, string? baseDirectory, out string filePath, out CueFileType fileType)
    {
        fileType = CueFileType.WAVE;
        if (rest.StartsWith('"'))
        {
            int endQuote = rest.IndexOf('"', 1);
            if (endQuote > 1)
            {
                filePath = rest[1..endQuote];
                var typePart = rest[(endQuote + 1)..].Trim();
                Enum.TryParse(typePart, ignoreCase: true, out fileType);
            }
            else
            {
                filePath = rest.Trim('"');
            }
        }
        else
        {
            var lastSpace = rest.LastIndexOf(' ');
            if (lastSpace > 0)
            {
                filePath = rest[..lastSpace].Trim();
                Enum.TryParse(rest[(lastSpace + 1)..].Trim(), ignoreCase: true, out fileType);
            }
            else
            {
                filePath = rest;
            }
        }

        if (!string.IsNullOrEmpty(baseDirectory) && !Path.IsPathRooted(filePath))
        {
            var combined = Path.Combine(baseDirectory, filePath);
            if (File.Exists(combined))
            {
                filePath = Path.GetFullPath(combined);
            }
        }
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }

    private static string EscapeQuotes(string s) => s.Replace("\"", "'");
}
