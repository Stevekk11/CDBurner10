namespace CDBurner10.Core.Burning;

public enum BurnPhase
{
    Preparing,
    Converting,
    Burning,
    ClosingSession,
    Verifying,
    Erasing,
    Grabbing,
    Completed,
    Cancelled,
    Failed
}

public sealed record BurnProgress(
    BurnPhase Phase,
    int Percent,
    int BufferPercent = 0,
    TimeSpan TimeRemaining = default,
    int CurrentTrack = 0,
    int TotalTracks = 0,
    string Message = "",
    int ReadErrors = 0);

public readonly record struct LbaRange(long StartLba, long EndLba)
{
    public long SectorCount => EndLba - StartLba + 1;

    public override string ToString() =>
        StartLba == EndLba
            ? $"LBA {StartLba}"
            : $"LBA {StartLba}..{EndLba} ({SectorCount})";
}

public sealed class BadSectorTracker
{
    private readonly List<LbaRange> _ranges = [];

    public int BadSectorCount { get; private set; }

    public IReadOnlyList<LbaRange> Ranges => _ranges;

    public void RecordBadSector(long lba)
    {
        BadSectorCount++;
        if (_ranges.Count > 0 && _ranges[^1].EndLba + 1 == lba)
        {
            var last = _ranges[^1];
            _ranges[^1] = new LbaRange(last.StartLba, lba);
        }
        else if (_ranges.Count == 0 || _ranges[^1].EndLba != lba)
        {
            _ranges.Add(new LbaRange(lba, lba));
        }
    }

    public string FormatSummary(int maxRanges = 10)
    {
        if (_ranges.Count == 0)
            return "none";

        var shown = _ranges.Take(Math.Max(1, maxRanges)).Select(r => r.ToString());
        string joined = string.Join(", ", shown);
        return _ranges.Count > maxRanges
            ? $"{joined}, ... (+{_ranges.Count - maxRanges} more range(s))"
            : joined;
    }
}

public sealed record EraseProgress(
    int Percent,
    TimeSpan TimeRemaining,
    string Message = "");

public sealed record ItemAddError(
    string FilePath,
    string Message,
    Exception? Exception = null,
    int MaxFileNameLength = 0);

public sealed class FileTooBigException : Exception
{
    public string FilePath { get; }
    public long FileSize { get; }

    public FileTooBigException(string filePath, long fileSize)
        : base($"File '{Path.GetFileName(filePath)}' ({fileSize:N0} bytes) exceeds the 4 GiB ISO9660/Joliet limit. Switch to UDF file system.")
    {
        FilePath = filePath;
        FileSize = fileSize;
    }
}

public sealed class FileNameTooLongException : Exception
{
    public string FileName { get; }
    public int MaxLength { get; }

    public FileNameTooLongException(string fileName, int maxLength)
        : base($"File name '{fileName}' exceeds the maximum length of {maxLength} characters for the selected file system.")
    {
        FileName = fileName;
        MaxLength = maxLength;
    }
}
