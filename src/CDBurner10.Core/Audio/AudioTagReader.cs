using System.Globalization;

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
        }
        catch
        {
            // Fallback to BASS for duration/CD-Text if TagLib doesn't support the format
        }

        if (duration <= TimeSpan.Zero)
        {
            duration = GetDurationViaBass(filePath);
        }

        if (PlaylistParser.IsCda(filePath))
        {
            var cdTextTitle = BassAudioEngine.GetCdTextTrackTitle(filePath);
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

    private static TimeSpan GetDurationViaBass(string filePath)
    {
        try
        {
            BassAudioEngine.Initialize();
            int stream = NativeBass.BASS_StreamCreateFile(false, filePath, 0, 0, NativeBass.BASS_STREAM_DECODE | NativeBass.BASS_UNICODE);
            if (stream == 0) return TimeSpan.Zero;
            try
            {
                long len = NativeBass.BASS_ChannelGetLength(stream, NativeBass.BASS_POS_BYTE);
                if (len <= 0) return TimeSpan.Zero;
                double secs = NativeBass.BASS_ChannelBytes2Seconds(stream, len);
                return secs > 0 ? TimeSpan.FromSeconds(secs) : TimeSpan.Zero;
            }
            finally
            {
                NativeBass.BASS_StreamFree(stream);
            }
        }
        catch
        {
            return TimeSpan.Zero;
        }
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
