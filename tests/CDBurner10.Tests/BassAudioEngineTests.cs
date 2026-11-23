using CDBurner10.Core.Audio;

namespace CDBurner10.Tests;

public sealed class BassAudioEngineTests
{
    [Fact]
    public void Initialize_ExtensionsCheck_AndShutdown_SucceedsLive()
    {
        try
        {
            BassAudioEngine.Initialize();
            Assert.True(BassAudioEngine.IsInitialized);

            var audioExts = BassAudioEngine.GetSupportedExtensions(includePlaylists: false);
            Assert.Contains(".mp3", audioExts);
            Assert.Contains(".flac", audioExts);
            Assert.Contains(".wav", audioExts);
            Assert.Contains(".ogg", audioExts);
            Assert.Contains(".opus", audioExts);
            Assert.Contains(".wma", audioExts);
            Assert.DoesNotContain(".m3u", audioExts);

            var withPlaylists = BassAudioEngine.GetSupportedExtensions(includePlaylists: true);
            Assert.Contains(".m3u", withPlaylists);
            Assert.Contains(".m3u8", withPlaylists);
            Assert.Contains(".wpl", withPlaylists);
            Assert.Contains(".nra", withPlaylists);
            Assert.Contains(".cue", withPlaylists);

            var filters = BassAudioEngine.GetSupportedExtensions(includePlaylists: false, formatAsFilter: true);
            Assert.Contains("*.flac", filters);
            Assert.Contains("*.mp3", filters);

            Assert.True(BassAudioEngine.IsAudioFile(@"C:\Music\track01.FLAC"));
            Assert.True(BassAudioEngine.IsAudioFile(@"C:\Music\track02.mp3"));
            Assert.True(BassAudioEngine.IsAudioFile(@"C:\Music\track03.aac"));
            Assert.False(BassAudioEngine.IsAudioFile(@"C:\Docs\readme.txt"));
            Assert.False(BassAudioEngine.IsAudioFile(""));
        }
        finally
        {
            BassAudioEngine.Shutdown();
            Assert.False(BassAudioEngine.IsInitialized);
        }
    }

    [Fact]
    public void NoClosedSourceBassDllsExistInOutput()
    {
        var bassDlls = Directory.GetFiles(AppContext.BaseDirectory, "bass*.dll");
        Assert.Empty(bassDlls);
    }

    [Fact]
    public void CrossfadingAudioConverter_ConvertsSplitsAndCrossfadesWithoutBass()
    {
        using var tmp = new TempDirectory();
        var wav1 = tmp.CreateSyntheticWav("t1.wav", 2000); // 2 seconds
        var wav2 = tmp.CreateSyntheticWav("t2.wav", 2000); // 2 seconds

        var created = new List<string>();
        try
        {
            // 1. Single convert with pre/post gap
            using (var conv = new CrossfadingAudioConverter { TargetSampleRate = 44100 })
            {
                var outPath = conv.ToWav(wav1, secondsPreGap: 0.5f, secondsPostGap: 0.5f);
                created.Add(outPath);
                Assert.True(File.Exists(outPath));
                var tag = AudioTagReader.Read(outPath);
                Assert.InRange(tag.Duration.TotalSeconds, 2.8, 3.2);
            }

            // 2. CUE split into 2 parts
            using (var splitter = new CrossfadingAudioConverter { TargetSampleRate = 44100 })
            {
                var parts = splitter.ToWav(wav1, [0.0, 1.0], 0f, 0f);
                created.AddRange(parts);
                Assert.Equal(2, parts.Length);
                Assert.All(parts, p => Assert.True(File.Exists(p)));
            }

            // 3. Crossfade between 2 tracks
            using (var xf = new CrossfadingAudioConverter
            {
                TargetSampleRate = 44100,
                Items = [wav1, wav2],
                CrossfadeLength = TimeSpan.FromSeconds(1)
            })
            {
                var out1 = xf.NextToWav();
                var out2 = xf.NextToWav();
                created.Add(out1);
                created.Add(out2);
                Assert.True(File.Exists(out1));
                Assert.True(File.Exists(out2));
                var d1 = AudioTagReader.Read(out1).Duration.TotalSeconds;
                var d2 = AudioTagReader.Read(out2).Duration.TotalSeconds;
                // 2s + 2s with 1s crossfade overlap => ~3.0s total (1.5s + 1.5s)
                Assert.InRange(d1 + d2, 2.8, 3.2);
            }
        }
        finally
        {
            foreach (var f in created)
            {
                try { File.Delete(f); } catch { }
            }
        }
    }

    [Fact]
    public void CdAudioDevice_ParseCdTextBuffer_ExtractsAlbumAndTrackTitles()
    {
        // Build minimal Red Book CD-Text buffer: 4-byte header + 2 packs (18 bytes each)
        var buf = new byte[4 + 36];
        buf[0] = 0;
        buf[1] = 38; // length excluding first 2 bytes

        // Pack 0: packType=0x80 (TITLE), track=0, text="Album\0Song1\0" (12 bytes)
        buf[4] = 0x80;
        buf[5] = 0;
        var p0 = System.Text.Encoding.ASCII.GetBytes("Album\0Song1\0");
        Array.Copy(p0, 0, buf, 8, p0.Length);

        // Pack 1: packType=0x81 (PERFORMER), track=1, text="Artist1\0"
        buf[22] = 0x81;
        buf[23] = 1;
        var p1 = System.Text.Encoding.ASCII.GetBytes("Artist1\0");
        Array.Copy(p1, 0, buf, 26, p1.Length);

        var dict = CdAudioDevice.ParseCdTextBuffer(buf);
        Assert.Equal("Album", dict["TITLE0"]);
        Assert.Equal("Song1", dict["TITLE1"]);
        Assert.Equal("Artist1", dict["PERFORMER1"]);
    }
}
