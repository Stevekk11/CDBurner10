using System.Diagnostics;
using System.Globalization;
using CDBurner10.Core.Dvd;
using NAudio.Wave;

namespace CDBurner10.Core.Audio;

public sealed record AudioTagInfo(
    string Title,
    string Artist,
    string Album,
    string AlbumArtist,
    string Genre,
    int TrackNumber,
    TimeSpan Duration,
    int BitrateKbps,
    string Format,
    double? ReplayGainTrackDb,
    double? ReplayGainAlbumDb,
    string? EmbeddedCueSheet);

public static class AudioTagReader
{
    public static AudioTagInfo Read(string filePath)
    {
        string fallbackTitle = Path.GetFileNameWithoutExtension(filePath);
        string format = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();

        string title = fallbackTitle;
        string artist = string.Empty;
        string album = string.Empty;
        string albumArtist = string.Empty;
        string genre = string.Empty;
        int trackNumber = 0;
        TimeSpan duration = TimeSpan.Zero;
        int bitrate = 0;
        double? rgTrack = null;
        double? rgAlbum = null;
        string? embeddedCue = null;

        try
        {
            using var tfile = TagLib.File.Create(filePath);
            if (!string.IsNullOrWhiteSpace(tfile.Tag.Title)) title = tfile.Tag.Title.Trim();
            artist = tfile.Tag.FirstPerformer?.Trim() ?? string.Empty;
            album = tfile.Tag.Album?.Trim() ?? string.Empty;
            albumArtist = tfile.Tag.FirstAlbumArtist?.Trim() ?? string.Empty;
            genre = tfile.Tag.FirstGenre?.Trim() ?? string.Empty;
            trackNumber = (int)tfile.Tag.Track;
            duration = tfile.Properties.Duration;
            bitrate = tfile.Properties.AudioBitrate;

            if (!double.IsNaN(tfile.Tag.ReplayGainTrackGain))
                rgTrack = tfile.Tag.ReplayGainTrackGain;
            if (!double.IsNaN(tfile.Tag.ReplayGainAlbumGain))
                rgAlbum = tfile.Tag.ReplayGainAlbumGain;

            // Check Xiph / APE / ID3v2 custom tags for CUE sheet or ReplayGain fallback
            if (tfile.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                var cue = xiph.GetFirstField("CUESHEET");
                if (!string.IsNullOrWhiteSpace(cue)) embeddedCue = cue;
                rgTrack ??= ParseReplayGainDb(xiph.GetFirstField("REPLAYGAIN_TRACK_GAIN"));
                rgAlbum ??= ParseReplayGainDb(xiph.GetFirstField("REPLAYGAIN_ALBUM_GAIN"));
            }
            if (tfile.GetTag(TagLib.TagTypes.Ape) is TagLib.Ape.Tag ape)
            {
                rgTrack ??= ParseReplayGainDb(ape.GetItem("REPLAYGAIN_TRACK_GAIN")?.ToString());
                rgAlbum ??= ParseReplayGainDb(ape.GetItem("REPLAYGAIN_ALBUM_GAIN")?.ToString());
            }
        }
        catch
        {
            // Fallback to NAudio / ffprobe for duration if TagLib doesn't support the format
        }

        if (duration <= TimeSpan.Zero)
        {
            duration = GetDurationFallback(filePath);
        }

        if (PlaylistParser.IsCda(filePath))
        {
            var cdTextTitle = CdAudioDevice.GetCdTextTrackTitle(filePath);
            if (!string.IsNullOrWhiteSpace(cdTextTitle))
            {
                title = cdTextTitle;
            }
            format = "CD-DA";
            bitrate = 1411;
        }

        return new AudioTagInfo(
            title,
            artist,
            album,
            albumArtist,
            genre,
            trackNumber,
            duration,
            bitrate,
            format,
            rgTrack,
            rgAlbum,
            embeddedCue);
    }

    public static (double? TrackGainDb, double? AlbumGainDb) ReadReplayGain(string filePath)
    {
        var info = Read(filePath);
        return (info.ReplayGainTrackDb, info.ReplayGainAlbumDb);
    }

    private static TimeSpan GetDurationFallback(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return TimeSpan.Zero;

        // 1. Try NAudio (handles WAV, MP3, WMA, M4A, AAC, AIFF via Windows Media Foundation)
        try
        {
            using var reader = new AudioFileReader(filePath);
            if (reader.TotalTime > TimeSpan.Zero)
                return reader.TotalTime;
        }
        catch
        {
            // Fall through to ffprobe
        }

        // 2. Try ffprobe (handles Opus, APE, WavPack, MPC, OGG, FLAC, etc.)
        try
        {
            string? ffprobe = DvdVideoConverter.FindFfprobe();
            if (string.IsNullOrWhiteSpace(ffprobe)) return TimeSpan.Zero;

            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            psi.ArgumentList.Add(filePath);

            using var process = Process.Start(psi);
            if (process is null) return TimeSpan.Zero;
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);
            if (double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out double secs) && secs > 0)
                return TimeSpan.FromSeconds(secs);
        }
        catch
        {
            // Ignore probe failures
        }

        return TimeSpan.Zero;
    }

    private static double? ParseReplayGainDb(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Trim().Replace("dB", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double val)
            ? val
            : null;
    }
}
