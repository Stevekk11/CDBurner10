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
    string Message = "");

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
