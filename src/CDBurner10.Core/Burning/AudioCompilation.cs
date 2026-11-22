using System.Globalization;
using System.Text;
using System.Xml;
using CDBurner10.Core.Audio;
using CDBurner10.Core.Audio.Cue;
using CDBurner10.Core.Settings;

namespace CDBurner10.Core.Burning;

public sealed class AudioTrackItem
{
    public string FileName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int TrackNumber { get; set; }
    public TimeSpan Duration { get; set; }
    public int BitrateKbps { get; set; }
    public string Format { get; set; } = string.Empty;
    public string CDTextTitle { get; set; } = string.Empty;
    public string CDTextArtist { get; set; } = string.Empty;
    public TimeSpan PreGap { get; set; } = TimeSpan.Zero;
    public TimeSpan PostGap { get; set; } = TimeSpan.Zero;
    public double? ReplayGainTrackDb { get; set; }
    public double? ReplayGainAlbumDb { get; set; }
    public bool IsCda => PlaylistParser.IsCda(FileName);

    public AudioTrackItem() { }

    public static AudioTrackItem FromFile(string filePath, bool autoPopulateCdText = true)
    {
        if (!File.Exists(filePath) && !filePath.StartsWith("MixedMode:", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException($"Audio file not found: '{filePath}'", filePath);

        var tag = AudioTagReader.Read(filePath);
        var item = new AudioTrackItem
        {
            FileName = filePath,
            Title = tag.Title,
            Artist = tag.Artist,
            Album = tag.Album,
            AlbumArtist = tag.AlbumArtist,
            Genre = tag.Genre,
            TrackNumber = tag.TrackNumber,
            Duration = tag.Duration,
            BitrateKbps = tag.BitrateKbps,
            Format = tag.Format,
            ReplayGainTrackDb = tag.ReplayGainTrackDb,
            ReplayGainAlbumDb = tag.ReplayGainAlbumDb
        };

        if (autoPopulateCdText)
        {
            item.CDTextTitle = StripToCdTextSize(tag.Title);
            item.CDTextArtist = StripToCdTextSize(tag.Artist);
        }

        return item;
    }

    public static string StripToCdTextSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var trimmed = text.Trim();
        return trimmed.Length > 32 ? trimmed[..32] : trimmed;
    }
}

public sealed class AudioCompilation
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public List<AudioTrackItem> Tracks { get; } = [];

    public TimeSpan TotalDuration =>
        TimeSpan.FromSeconds(Tracks.Sum(t => t.Duration.TotalSeconds + t.PreGap.TotalSeconds + t.PostGap.TotalSeconds));

    public AudioCompilation() { }

    public AudioCompilation(IEnumerable<AudioTrackItem> tracks)
    {
        Tracks.AddRange(tracks);
    }

    public ItemAddError[] Load(string axpFilePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var errors = new List<ItemAddError>();
        Tracks.Clear();

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            ValidationType = ValidationType.None
        };

        using var fs = new FileStream(axpFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = XmlReader.Create(fs, settings);

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.Name.Equals("compilation", StringComparison.OrdinalIgnoreCase))
            {
                Artist = reader.GetAttribute("artist") ?? string.Empty;
                Title = reader.GetAttribute("title") ?? string.Empty;
            }
            else if (reader.Name.Equals("track", StringComparison.OrdinalIgnoreCase))
            {
                if (fs.Length > 0) progress?.Report(fs.Position * 100.0 / fs.Length);
                var path = reader.GetAttribute("path") ?? reader.GetAttribute("Path") ?? string.Empty;
                try
                {
                    var track = AudioTrackItem.FromFile(path, autoPopulateCdText: false);
                    track.CDTextTitle = reader.GetAttribute("title") ?? AudioTrackItem.StripToCdTextSize(track.Title);
                    track.CDTextArtist = reader.GetAttribute("artist") ?? AudioTrackItem.StripToCdTextSize(track.Artist);
                    if (double.TryParse(reader.GetAttribute("pregap"), NumberStyles.Float, CultureInfo.InvariantCulture, out var pre))
                        track.PreGap = TimeSpan.FromSeconds(pre);
                    if (double.TryParse(reader.GetAttribute("postgap"), NumberStyles.Float, CultureInfo.InvariantCulture, out var post))
                        track.PostGap = TimeSpan.FromSeconds(post);
                    Tracks.Add(track);
                }
                catch (Exception ex)
                {
                    errors.Add(new ItemAddError(path, ex.Message, ex));
                }
            }
        }

        progress?.Report(100);
        return errors.ToArray();
    }

    public void Save(string axpFilePath, IProgress<double>? progress = null, bool copyTempRipFiles = true)
    {
        var dir = Path.GetDirectoryName(axpFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempRipDir = SettingsService.Default.GetTempRipDirectory();
        using var fs = new FileStream(axpFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = XmlWriter.Create(fs, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 });

        writer.WriteStartDocument(standalone: true);
        writer.WriteDocType("layout", "http://www.cdburnerxp.se/help/audio.dtd", null, null);
        writer.WriteProcessingInstruction("xml-stylesheet", "type='text/xsl' href='http://www.cdburnerxp.se/help/compilation.xsl'");
        writer.WriteComment("audio compilation created by CDBurner10 (CDBurnerXP compatible)");
        writer.WriteStartElement("layout");
        writer.WriteAttributeString("type", "Audio");
        writer.WriteAttributeString("version", "10.0.0.0");
        writer.WriteAttributeString("date", DateTime.Now.ToShortDateString());
        writer.WriteAttributeString("time", DateTime.Now.ToShortTimeString());

        writer.WriteStartElement("compilation");
        writer.WriteAttributeString("name", "audio-template");
        writer.WriteAttributeString("title", Title);
        writer.WriteAttributeString("artist", Artist);

        for (int i = 0; i < Tracks.Count; i++)
        {
            var track = Tracks[i];
            string pathToSave = track.FileName;
            if (copyTempRipFiles &&
                !string.IsNullOrEmpty(dir) &&
                track.FileName.StartsWith(tempRipDir, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(track.FileName))
            {
                try
                {
                    var dest = Path.Combine(dir, Path.GetFileName(track.FileName));
                    File.Copy(track.FileName, dest, overwrite: true);
                    pathToSave = dest;
                }
                catch { }
            }

            writer.WriteStartElement("track");
            writer.WriteAttributeString("path", pathToSave);
            writer.WriteAttributeString("title", track.CDTextTitle);
            writer.WriteAttributeString("artist", track.CDTextArtist);
            writer.WriteAttributeString("pregap", track.PreGap.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("postgap", track.PostGap.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("number", (i + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();

            progress?.Report((i + 1) * 100.0 / Math.Max(1, Tracks.Count));
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
        progress?.Report(100);
    }

    public void SaveAsCue(string filePath)
    {
        var cue = new CueSheet { Performer = Artist, Title = Title };
        foreach (var track in Tracks)
        {
            var ct = cue.AddTrack(
                string.IsNullOrWhiteSpace(track.CDTextTitle) ? track.Title : track.CDTextTitle,
                string.IsNullOrWhiteSpace(track.CDTextArtist) ? track.Artist : track.CDTextArtist,
                track.FileName,
                CueFileType.WAVE);
            ct.AddIndex(1, 0, 0, 0);
            if (track.PreGap > TimeSpan.Zero)
                ct.PreGap = new CueIndex(0, track.PreGap.Minutes, track.PreGap.Seconds, 0);
            if (track.PostGap > TimeSpan.Zero)
                ct.PostGap = new CueIndex(0, track.PostGap.Minutes, track.PostGap.Seconds, 0);
        }
        cue.Save(filePath, Encoding.UTF8);
    }

    public void SaveAsM3u(string filePath)
    {
        bool utf8 = filePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        using var sw = new StreamWriter(filePath, false, utf8 ? Encoding.UTF8 : Encoding.Default);
        sw.WriteLine("#EXTM3U");
        foreach (var track in Tracks)
        {
            sw.WriteLine($"#EXTINF:{(int)Math.Round(track.Duration.TotalSeconds)},{track.Artist} - {track.Title}");
            sw.WriteLine(track.FileName);
        }
    }

    public ItemAddError[] ImportPlaylist(string playlistPath, bool autoPopulateCdText = true)
    {
        var errors = new List<ItemAddError>();
        var files = PlaylistParser.GetFilesFromPlaylist(playlistPath);
        foreach (var file in files)
        {
            try
            {
                Tracks.Add(AudioTrackItem.FromFile(file, autoPopulateCdText));
            }
            catch (Exception ex)
            {
                errors.Add(new ItemAddError(file, ex.Message, ex));
            }
        }
        return errors.ToArray();
    }
}
