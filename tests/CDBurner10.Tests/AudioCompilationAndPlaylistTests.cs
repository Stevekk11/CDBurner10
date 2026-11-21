using System.Text;
using CDBurner10.Core.Audio;
using CDBurner10.Core.Audio.Cue;
using CDBurner10.Core.Burning;

namespace CDBurner10.Tests;

public sealed class AudioCompilationAndPlaylistTests
{
    [Fact]
    public void AudioCompilation_SaveAndLoad_RoundTripPreservesMetadataAndGaps()
    {
        using var tmp = new TempDirectory();
        var wav1 = tmp.CreateSyntheticWav("track1.wav", 300);
        var wav2 = tmp.CreateSyntheticWav("track2.wav", 400);
        var axpPath = Path.Combine(tmp.Path, "album.axp");

        var comp = new AudioCompilation
        {
            Title = "Greatest Hits",
            Artist = "Test Band"
        };

        var t1 = AudioTrackItem.FromFile(wav1);
        t1.CDTextTitle = "Opening Track";
        t1.CDTextArtist = "Test Band";
        t1.PreGap = TimeSpan.FromSeconds(2);
        t1.PostGap = TimeSpan.FromSeconds(1);

        var t2 = AudioTrackItem.FromFile(wav2);
        t2.CDTextTitle = "Second Track";
        t2.CDTextArtist = "Guest Artist";
        t2.PreGap = TimeSpan.Zero;
        t2.PostGap = TimeSpan.FromSeconds(3);

        comp.Tracks.Add(t1);
        comp.Tracks.Add(t2);

        comp.Save(axpPath);
        Assert.True(File.Exists(axpPath));

        var loaded = new AudioCompilation();
        var errors = loaded.Load(axpPath);

        Assert.Empty(errors);
        Assert.Equal("Greatest Hits", loaded.Title);
        Assert.Equal("Test Band", loaded.Artist);
        Assert.Equal(2, loaded.Tracks.Count);

        Assert.Equal(wav1, loaded.Tracks[0].FileName);
        Assert.Equal("Opening Track", loaded.Tracks[0].CDTextTitle);
        Assert.Equal("Test Band", loaded.Tracks[0].CDTextArtist);
        Assert.Equal(TimeSpan.FromSeconds(2), loaded.Tracks[0].PreGap);
        Assert.Equal(TimeSpan.FromSeconds(1), loaded.Tracks[0].PostGap);

        Assert.Equal(wav2, loaded.Tracks[1].FileName);
        Assert.Equal("Second Track", loaded.Tracks[1].CDTextTitle);
        Assert.Equal("Guest Artist", loaded.Tracks[1].CDTextArtist);
        Assert.Equal(TimeSpan.FromSeconds(3), loaded.Tracks[1].PostGap);
    }

    [Fact]
    public void CueSheet_ExportFromAudioCompilation_AndReImport_RoundTrips()
    {
        using var tmp = new TempDirectory();
        var wav1 = tmp.CreateSyntheticWav("one.wav", 200);
        var wav2 = tmp.CreateSyntheticWav("two.wav", 200);
        var cuePath = Path.Combine(tmp.Path, "disc.cue");

        var comp = new AudioCompilation { Title = "Cue Album", Artist = "Cue Artist" };
        var t1 = AudioTrackItem.FromFile(wav1);
        t1.CDTextTitle = "Song One";
        t1.CDTextArtist = "Cue Artist";
        t1.PreGap = TimeSpan.FromSeconds(2);
        var t2 = AudioTrackItem.FromFile(wav2);
        t2.CDTextTitle = "Song Two";
        t2.CDTextArtist = "Cue Artist";
        t2.PostGap = TimeSpan.FromSeconds(4);

        comp.Tracks.Add(t1);
        comp.Tracks.Add(t2);
        comp.SaveAsCue(cuePath);

        var parsed = CueSheet.LoadFromFile(cuePath);
        Assert.Equal("Cue Album", parsed.Title);
        Assert.Equal("Cue Artist", parsed.Performer);
        Assert.Equal(2, parsed.Tracks.Count);
        Assert.Equal("Song One", parsed.Tracks[0].Title);
        Assert.Equal(wav1, parsed.Tracks[0].DataFileName);
        Assert.Equal(new CueIndex(0, 0, 2, 0), parsed.Tracks[0].PreGap);
        Assert.Equal("Song Two", parsed.Tracks[1].Title);
        Assert.Equal(wav2, parsed.Tracks[1].DataFileName);
        Assert.Equal(new CueIndex(0, 0, 4, 0), parsed.Tracks[1].PostGap);

        // Also test PlaylistParser.GetFilesFromPlaylist for .cue
        var cueFiles = PlaylistParser.GetFilesFromPlaylist(cuePath);
        Assert.Equal(2, cueFiles.Length);
        Assert.Contains(wav1, cueFiles);
        Assert.Contains(wav2, cueFiles);
    }

    [Fact]
    public void PlaylistParser_ParsesM3u_M3u8_Wpl_And_Nra()
    {
        using var tmp = new TempDirectory();
        var wav1 = tmp.CreateSyntheticWav("song1.wav", 150);
        var wav2 = tmp.CreateSyntheticWav("sub/song2.wav", 150);

        // M3U (with relative path)
        var m3uPath = tmp.CreateTextFile("list.m3u", $"#EXTM3U\n#EXTINF:1,Song 1\nsong1.wav\nsub\\song2.wav\n", Encoding.Default);
        var m3uFiles = PlaylistParser.GetFilesFromPlaylist(m3uPath);
        Assert.Equal(2, m3uFiles.Length);
        Assert.Equal(Path.GetFullPath(wav1), Path.GetFullPath(m3uFiles[0]));
        Assert.Equal(Path.GetFullPath(wav2), Path.GetFullPath(m3uFiles[1]));

        // M3U8 (UTF-8)
        var wavUnicode = tmp.CreateSyntheticWav("café_track.wav", 150);
        var m3u8Path = tmp.CreateTextFile("list.m3u8", "#EXTM3U\ncafé_track.wav\n", Encoding.UTF8);
        var m3u8Files = PlaylistParser.GetFilesFromPlaylist(m3u8Path);
        Assert.Single(m3u8Files);
        Assert.Equal(Path.GetFullPath(wavUnicode), Path.GetFullPath(m3u8Files[0]));

        // WPL (Windows Media Playlist XML)
        var wplContent = $"""
            <?wpl version="1.0"?>
            <smil>
              <body>
                <seq>
                  <media src="song1.wav"/>
                  <media src="{wav2}"/>
                </seq>
              </body>
            </smil>
            """;
        var wplPath = tmp.CreateTextFile("list.wpl", wplContent, Encoding.UTF8);
        var wplFiles = PlaylistParser.GetFilesFromPlaylist(wplPath);
        Assert.Equal(2, wplFiles.Length);

        // NRA (Nero binary audio compilation containing UTF-16LE and ASCII drive paths)
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x00, 0x01, 0x02, 0x00 });
        ms.Write(Encoding.Unicode.GetBytes(wav1 + "\0"));
        ms.Write(new byte[] { 0x00, 0x00 });
        ms.Write(Encoding.ASCII.GetBytes(wav2 + "\0"));
        var nraPath = tmp.CreateFile("nero.nra", ms.ToArray());
        var nraFiles = PlaylistParser.GetFilesFromPlaylist(nraPath);
        Assert.Equal(2, nraFiles.Length);
        Assert.Equal(wav1, nraFiles[0]);
        Assert.Equal(wav2, nraFiles[1]);
    }

    [Fact]
    public void StripToCdTextSize_TruncatesAt32CharsAndTrims()
    {
        Assert.Equal(string.Empty, AudioTrackItem.StripToCdTextSize(null));
        Assert.Equal(string.Empty, AudioTrackItem.StripToCdTextSize("   "));
        Assert.Equal("Short Title", AudioTrackItem.StripToCdTextSize("  Short Title  "));

        string exact32 = new string('A', 32);
        Assert.Equal(exact32, AudioTrackItem.StripToCdTextSize(exact32));

        string longText = "12345678901234567890123456789012_EXTRA_CHARS";
        var truncated = AudioTrackItem.StripToCdTextSize(longText);
        Assert.Equal(32, truncated.Length);
        Assert.Equal("12345678901234567890123456789012", truncated);
    }
}
