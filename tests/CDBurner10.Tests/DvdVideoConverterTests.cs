using CDBurner10.Core.Dvd;

namespace CDBurner10.Tests;

public sealed class DvdVideoConverterTests
{
    [Fact]
    public void NormalizeSource_WhenVideoTsFolder_ReturnsDiscRoot()
    {
        using var tmp = new TempDirectory();
        string videoTs = Path.Combine(tmp.Path, "VIDEO_TS");
        Directory.CreateDirectory(videoTs);

        Assert.Equal(
            Path.GetFullPath(tmp.Path),
            DvdVideoConverter.NormalizeSource(videoTs));
    }

    [Fact]
    public void NormalizeSource_WhenDiscRoot_LeavesPathAlone()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "VIDEO_TS"));

        Assert.Equal(
            Path.GetFullPath(tmp.Path),
            DvdVideoConverter.NormalizeSource(tmp.Path));
    }

    [Fact]
    public void ValidateSource_ThrowsForMissingPath()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.iso");
        Assert.Throws<InvalidOperationException>(() => DvdVideoConverter.ValidateSource(missing));
    }

    [Theory]
    [InlineData("eng", "English")]
    [InlineData("cze", "Czech")]
    [InlineData("slo", "Slovak")]
    [InlineData("deu", "German")]
    [InlineData("xx", "XX")]
    public void GetLanguageName_MapsDvdLanguageCodes(string code, string expected)
    {
        Assert.Equal(expected, DvdVideoConverter.GetLanguageName(code));
    }

    [Theory]
    [InlineData(1, "Mono")]
    [InlineData(2, "Stereo")]
    [InlineData(6, "5.1")]
    [InlineData(8, "7.1")]
    public void GetChannelName_MapsChannelCounts(int channels, string expected)
    {
        Assert.Equal(expected, DvdVideoConverter.GetChannelName(channels));
    }

    [Fact]
    public void GetLanguageName_HandlesMissingCode()
    {
        Assert.Equal("Unknown language", DvdVideoConverter.GetLanguageName(null));
        Assert.Equal("Unknown language", DvdVideoConverter.GetLanguageName(" "));
    }

    [Fact]
    public void ParseProbeJson_ParsesFormatStreamsAndTracks()
    {
        const string json = """
        {
          "format": { "duration": "5420.5" },
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "mpeg2video", "width": 720, "height": 576 },
            { "index": 1, "codec_type": "audio", "codec_name": "ac3", "channels": 6, "tags": { "language": "eng" } },
            { "index": 2, "codec_type": "audio", "codec_name": "ac3", "channels": 2, "tags": { "language": "cze" } },
            { "index": 3, "codec_type": "subtitle", "codec_name": "dvd_subtitle", "tags": { "language": "cze" } }
          ]
        }
        """;

        var info = DvdVideoConverter.ParseProbeJson(json);

        Assert.Equal(TimeSpan.FromSeconds(5420.5), info.Duration);
        Assert.Equal(720, info.Width);
        Assert.Equal(576, info.Height);
        Assert.Equal(2, info.AudioTracks.Count);
        Assert.Equal("English • 5.1 AC3", info.AudioTracks[0].DisplayName);
        Assert.Equal("Czech • Stereo AC3", info.AudioTracks[1].DisplayName);
        Assert.Single(info.SubtitleTracks);
        Assert.Equal("Czech (DVD subtitles)", info.SubtitleTracks[0].DisplayName);
    }

    [Fact]
    public void ParseProbeJson_FallsBackToStreamDurationWhenFormatDurationMissing()
    {
        const string json = """
        {
          "format": { "duration": "N/A" },
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "mpeg2video", "width": 720, "height": 480, "duration": "3600.25" }
          ]
        }
        """;

        var info = DvdVideoConverter.ParseProbeJson(json);
        Assert.Equal(TimeSpan.FromSeconds(3600.25), info.Duration);
    }

    [Fact]
    public void BuildFfmpegArguments_DeinterlacesAndOverlaysSubtitlesSafely()
    {
        var info = new DvdVideoInfo(
            TimeSpan.FromMinutes(90),
            720,
            576,
            [new DvdAudioTrack(1, 0, "ac3", 6, "eng", null)],
            [new DvdSubtitleTrack(2, 0, "dvd_subtitle", "cze")]);

        var options = new DvdMp4Options
        {
            TitleNumber = 2,
            AudioOrdinal = 0,
            SubtitleOrdinal = 0,
            Crf = 18,
            Preset = "slow",
            StereoDownmix = false,
            Deinterlace = true,
            MaxDuration = TimeSpan.FromSeconds(30)
        };

        var args = DvdVideoConverter.BuildFfmpegArguments(@"C:\movie.iso", @"C:\movie.mp4", info, options);

        Assert.Contains("-title", args);
        Assert.Equal("2", args[args.IndexOf("-title") + 1]);
        Assert.Contains("-filter_complex", args);
        Assert.Equal("[0:v]bwdif=0:-1:1[v0];[v0][0:s:0]overlay=eof_action=pass[v]", args[args.IndexOf("-filter_complex") + 1]);
        Assert.Equal("384k", args[args.IndexOf("-b:a") + 1]);
        Assert.DoesNotContain("-ac", args);
        Assert.Equal("30", args[args.IndexOf("-t") + 1]);
        Assert.Equal(@"C:\movie.mp4", args[^1]);
    }

    [Fact]
    public void BuildFfmpegArguments_WithoutSubtitles_UsesBwdifAndStereo192k()
    {
        var info = new DvdVideoInfo(
            TimeSpan.FromMinutes(90),
            720,
            480,
            [new DvdAudioTrack(1, 0, "ac3", 6, "eng", null)],
            []);

        var options = new DvdMp4Options
        {
            StereoDownmix = true,
            Deinterlace = true
        };

        var args = DvdVideoConverter.BuildFfmpegArguments(@"C:\movie.iso", @"C:\movie.mp4", info, options);

        Assert.DoesNotContain("-title", args);
        Assert.Equal("bwdif=0:-1:1", args[args.IndexOf("-vf") + 1]);
        Assert.Equal("192k", args[args.IndexOf("-b:a") + 1]);
        Assert.Equal("2", args[args.IndexOf("-ac") + 1]);
    }

    [Fact]
    public void FfmpegToolDiscovery_FindsInstalledFfmpeg()
    {
        Assert.True(DvdVideoConverter.IsAvailable);
        Assert.True(File.Exists(DvdVideoConverter.FfmpegPath));
        Assert.True(File.Exists(DvdVideoConverter.FfprobePath));
    }
}
