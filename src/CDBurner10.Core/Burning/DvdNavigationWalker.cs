using System.Diagnostics;
using CDBurner10.Core.Native;

namespace CDBurner10.Core.Burning;

public sealed record DvdTitleInfo(
    int TitleNumber,
    int Parts,
    TimeSpan Duration);

public sealed record DvdWalkResult(
    int TitleNumber,
    int Parts,
    long BlocksWritten,
    long BytesWritten,
    TimeSpan Elapsed);

/// <summary>
/// Walks a DVD-Video disc by following its own PGC/cell navigation program via
/// libdvdnav + libdvdread (the same way a standalone player or VLC does), rather
/// than performing a linear sector sweep. Every byte written to the output stream
/// is a 2048-byte MPEG-2 program stream block selected by libdvdnav.
/// </summary>
public static class DvdNavigationWalker
{
    public const int BlockSize = DvdNavInterop.DVD_VIDEO_LB_LEN;

    public static bool IsAvailable => DvdNavInterop.IsAvailable;

    /// <summary>
    /// Formats a Windows drive letter ("E", "E:", "E:\", "\\.\E:") into the device path
    /// form expected by libdvdnav/libdvdread ("E:"), while leaving ISO files and folder
    /// paths unchanged.
    /// </summary>
    public static string NormalizeOpenPath(string driveOrPath)
    {
        if (string.IsNullOrWhiteSpace(driveOrPath))
            throw new ArgumentException("DVD path cannot be empty.", nameof(driveOrPath));

        string s = driveOrPath.Trim();
        if (TryExtractDriveLetter(s, out char letter))
            return $"{letter}:";

        return Path.GetFullPath(s);
    }

    public static bool IsDvdVideoDrive(DriveModel drive)
    {
        if (string.IsNullOrWhiteSpace(drive.Letter))
            return false;

        try
        {
            string videoTs = $"{char.ToUpperInvariant(drive.Letter[0])}:\\VIDEO_TS";
            return Directory.Exists(videoTs) ||
                   File.Exists(Path.Combine(videoTs, "VIDEO_TS.IFO"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enumerates all titles on the DVD via dvdnav_get_number_of_titles,
    /// dvdnav_get_number_of_parts, and dvdnav_describe_title_chapters.
    /// </summary>
    public static IReadOnlyList<DvdTitleInfo> EnumerateTitles(string driveOrPath)
    {
        IntPtr dvd = OpenConfiguredHandle(driveOrPath);
        try
        {
            return EnumerateTitlesInternal(dvd);
        }
        finally
        {
            DvdNavInterop.dvdnav_close(dvd);
        }
    }

    /// <summary>
    /// Walks the specified title (or the longest title when targetTitle &lt;= 0) and writes
    /// the continuous MPEG-2 program stream blocks selected by libdvdnav into <paramref name="output"/>.
    /// </summary>
    public static DvdWalkResult WalkTitleToStream(
        string driveOrPath,
        Stream output,
        int targetTitle = 0,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        IntPtr dvd = OpenConfiguredHandle(driveOrPath);
        try
        {
            var titles = EnumerateTitlesInternal(dvd);
            if (titles.Count == 0)
                throw new InvalidOperationException("No playable DVD titles were found on the disc.");

            DvdTitleInfo selected = targetTitle > 0
                ? titles.FirstOrDefault(t => t.TitleNumber == targetTitle)
                  ?? throw new InvalidOperationException(
                      $"DVD title {targetTitle} does not exist (disc has {titles.Count} title(s)).")
                : SelectMainTitle(titles);

            if (DvdNavInterop.dvdnav_title_play(dvd, selected.TitleNumber) != DvdNavInterop.DVDNAV_STATUS_OK)
                ThrowNavError(dvd, $"Failed to start DVD title {selected.TitleNumber}");

            progress?.Report(new BurnProgress(
                BurnPhase.Grabbing,
                0,
                Message: $"Reading DVD title {selected.TitleNumber} ({selected.Parts} chapter(s)) via libdvdnav..."));

            var blockBuffer = new byte[BlockSize];
            long blocksWritten = 0;
            long bytesWritten = 0;
            int lastPercent = -1;
            var stopwatch = Stopwatch.StartNew();

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                int status = DvdNavInterop.dvdnav_get_next_block(dvd, blockBuffer, out int evt, out int len);
                if (status == DvdNavInterop.DVDNAV_STATUS_ERR)
                    ThrowNavError(dvd, "libdvdnav failed while reading next block");

                switch (evt)
                {
                    case DvdNavInterop.DVDNAV_BLOCK_OK:
                        if (len > 0)
                        {
                            output.Write(blockBuffer, 0, len);
                            blocksWritten++;
                            bytesWritten += len;
                        }

                        if ((blocksWritten & 0xFF) == 0) // Every 256 blocks (~512 KB)
                        {
                            ReportPosition(dvd, selected.TitleNumber, stopwatch.Elapsed, ref lastPercent, progress);
                        }
                        break;

                    case DvdNavInterop.DVDNAV_STILL_FRAME:
                        if (DvdNavInterop.dvdnav_still_skip(dvd) != DvdNavInterop.DVDNAV_STATUS_OK)
                            ThrowNavError(dvd, "libdvdnav failed to skip still frame");
                        break;

                    case DvdNavInterop.DVDNAV_WAIT:
                        if (DvdNavInterop.dvdnav_wait_skip(dvd) != DvdNavInterop.DVDNAV_STATUS_OK)
                            ThrowNavError(dvd, "libdvdnav failed to skip wait state");
                        break;

                    case DvdNavInterop.DVDNAV_STOP:
                        output.Flush();
                        progress?.Report(new BurnProgress(
                            BurnPhase.Completed,
                            100,
                            Message: $"Navigated DVD title {selected.TitleNumber} complete ({blocksWritten:N0} blocks)."));
                        return new DvdWalkResult(
                            selected.TitleNumber,
                            selected.Parts,
                            blocksWritten,
                            bytesWritten,
                            stopwatch.Elapsed);

                    case DvdNavInterop.DVDNAV_VTS_CHANGE:
                    case DvdNavInterop.DVDNAV_CELL_CHANGE:
                        // If the disc VM jumped out of the target title (e.g. post-command to menu)
                        // after we already read blocks, stop cleanly instead of looping in menus.
                        if (blocksWritten > 0 &&
                            DvdNavInterop.dvdnav_current_title_info(dvd, out int currentTitle, out _) == DvdNavInterop.DVDNAV_STATUS_OK &&
                            currentTitle != selected.TitleNumber)
                        {
                            output.Flush();
                            progress?.Report(new BurnProgress(
                                BurnPhase.Completed,
                                100,
                                Message: $"Navigated DVD title {selected.TitleNumber} complete ({blocksWritten:N0} blocks)."));
                            return new DvdWalkResult(
                                selected.TitleNumber,
                                selected.Parts,
                                blocksWritten,
                                bytesWritten,
                                stopwatch.Elapsed);
                        }
                        break;

                    default:
                        // Other events (NAV_PACKET, SPU/AUDIO_STREAM_CHANGE, CLUT, HIGHLIGHT, HOP_CHANNEL, NOP)
                        // are intentional no-ops for a copy operation.
                        break;
                }
            }
        }
        finally
        {
            DvdNavInterop.dvdnav_close(dvd);
        }
    }

    public static DvdWalkResult WalkTitleToFile(
        string driveOrPath,
        string outputFilePath,
        int targetTitle = 0,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var output = new FileStream(
            outputFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);

        return WalkTitleToStream(driveOrPath, output, targetTitle, progress, ct);
    }

    public static DvdTitleInfo SelectMainTitle(IReadOnlyList<DvdTitleInfo> titles)
    {
        if (titles.Count == 0)
            throw new InvalidOperationException("No DVD titles available.");

        return titles
            .OrderByDescending(t => t.Duration)
            .ThenByDescending(t => t.Parts)
            .ThenBy(t => t.TitleNumber)
            .First();
    }

    private static IntPtr OpenConfiguredHandle(string driveOrPath)
    {
        if (!IsAvailable)
            throw new InvalidOperationException(
                $"The bundled '{DvdNavInterop.DvdNavDllName}' and '{DvdNavInterop.DvdReadDllName}' libraries were not found next to the application.");

        string primary = NormalizeOpenPath(driveOrPath);
        IntPtr dvd = IntPtr.Zero;

        if (DvdNavInterop.dvdnav_open(out dvd, primary) != DvdNavInterop.DVDNAV_STATUS_OK || dvd == IntPtr.Zero)
        {
            // For optical drives, retry with Win32 device path \\.\X: if X: failed.
            if (TryExtractDriveLetter(driveOrPath, out char letter))
            {
                string win32Device = $@"\\.\{letter}:";
                if (DvdNavInterop.dvdnav_open(out dvd, win32Device) != DvdNavInterop.DVDNAV_STATUS_OK || dvd == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        $"libdvdnav could not open DVD drive '{primary}' or '{win32Device}'.");
                }
            }
            else
            {
                throw new InvalidOperationException($"libdvdnav could not open DVD source '{primary}'.");
            }
        }

        try
        {
            // Readahead OFF, PGC positioning ON (per specification).
            if (DvdNavInterop.dvdnav_set_readahead_flag(dvd, 0) != DvdNavInterop.DVDNAV_STATUS_OK)
                ThrowNavError(dvd, "Failed to disable libdvdnav readahead");

            if (DvdNavInterop.dvdnav_set_PGC_positioning_flag(dvd, 1) != DvdNavInterop.DVDNAV_STATUS_OK)
                ThrowNavError(dvd, "Failed to enable libdvdnav PGC positioning");

            return dvd;
        }
        catch
        {
            DvdNavInterop.dvdnav_close(dvd);
            throw;
        }
    }

    private static List<DvdTitleInfo> EnumerateTitlesInternal(IntPtr dvd)
    {
        if (DvdNavInterop.dvdnav_get_number_of_titles(dvd, out int titleCount) != DvdNavInterop.DVDNAV_STATUS_OK)
            ThrowNavError(dvd, "Failed to query number of DVD titles");

        var result = new List<DvdTitleInfo>(Math.Max(0, titleCount));
        for (int title = 1; title <= titleCount; title++)
        {
            int parts = 0;
            if (DvdNavInterop.dvdnav_get_number_of_parts(dvd, title, out int p) == DvdNavInterop.DVDNAV_STATUS_OK)
                parts = p;

            TimeSpan duration = TimeSpan.Zero;
            IntPtr timesPtr = IntPtr.Zero;
            try
            {
                uint chapterCount = DvdNavInterop.dvdnav_describe_title_chapters(dvd, title, out timesPtr, out ulong durationTicks);
                if (chapterCount > 0 && durationTicks > 0)
                {
                    // libdvdnav durations are in 90 kHz PTS ticks.
                    duration = TimeSpan.FromSeconds(durationTicks / 90000.0);
                }
            }
            finally
            {
                if (timesPtr != IntPtr.Zero)
                    DvdNavInterop.CrtFree(timesPtr);
            }

            result.Add(new DvdTitleInfo(title, parts, duration));
        }

        return result;
    }

    private static void ReportPosition(
        IntPtr dvd,
        int titleNumber,
        TimeSpan elapsed,
        ref int lastPercent,
        IProgress<BurnProgress>? progress)
    {
        if (progress is null) return;
        if (DvdNavInterop.dvdnav_get_position(dvd, out uint pos, out uint totalLen) != DvdNavInterop.DVDNAV_STATUS_OK || totalLen == 0)
            return;

        int percent = (int)Math.Clamp((double)pos / totalLen * 100.0, 0, 99);
        if (percent == lastPercent) return;
        lastPercent = percent;

        TimeSpan remaining = TimeSpan.Zero;
        if (pos > 0 && pos < totalLen && elapsed.TotalSeconds > 0.5)
        {
            double blocksPerSec = pos / elapsed.TotalSeconds;
            if (blocksPerSec > 0)
                remaining = TimeSpan.FromSeconds((totalLen - pos) / blocksPerSec);
        }

        progress.Report(new BurnProgress(
            BurnPhase.Grabbing,
            percent,
            TimeRemaining: remaining,
            Message: $"Reading DVD title {titleNumber} (navigated)... {percent}%"));
    }

    private static void ThrowNavError(IntPtr dvd, string context)
    {
        string detail = DvdNavInterop.GetErrorString(dvd);
        throw new InvalidOperationException($"{context}: {detail}");
    }

    private static bool TryExtractDriveLetter(string input, out char letter)
    {
        letter = '\0';
        string s = input.Trim();
        if (s.StartsWith(@"\\.\", StringComparison.Ordinal))
            s = s[4..];

        if (s.Length == 1 && char.IsAsciiLetter(s[0]))
        {
            letter = char.ToUpperInvariant(s[0]);
            return true;
        }
        if (s.Length == 2 && char.IsAsciiLetter(s[0]) && s[1] == ':')
        {
            letter = char.ToUpperInvariant(s[0]);
            return true;
        }
        if (s.Length == 3 && char.IsAsciiLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/'))
        {
            letter = char.ToUpperInvariant(s[0]);
            return true;
        }
        return false;
    }
}
