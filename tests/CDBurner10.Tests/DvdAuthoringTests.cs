using System.Diagnostics;
using System.Text.Json;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Dvd;

namespace CDBurner10.Tests;

public class DvdAuthoringTests
{
    [Theory]
    [InlineData(DvdTargetDisc.Dvd5_4_7GB, DvdQualityPreset.High, 1, 8000)]
    [InlineData(DvdTargetDisc.Dvd5_4_7GB, DvdQualityPreset.Medium, 1, 5500)]
    [InlineData(DvdTargetDisc.Dvd5_4_7GB, DvdQualityPreset.Low, 1, 3500)]
    [InlineData(DvdTargetDisc.Dvd9_8_5GB, DvdQualityPreset.High, 1, 8000)]
    [InlineData(DvdTargetDisc.Dvd9_8_5GB, DvdQualityPreset.Medium, 1, 5500)]
    [InlineData(DvdTargetDisc.Dvd9_8_5GB, DvdQualityPreset.Low, 1, 3500)]
    public void CalculateVideoBitrateKbps_FixedPresets_ReturnExpectedBitrate(
        DvdTargetDisc targetDisc,
        DvdQualityPreset preset,
        int audioStreams,
        int expectedKbps)
    {
        int kbps = DvdMediaProber.CalculateVideoBitrateKbps(
            TimeSpan.FromMinutes(90),
            targetDisc,
            preset,
            audioStreams);

        Assert.Equal(expectedKbps, kbps);
    }

    [Fact]
    public void CalculateVideoBitrateKbps_BestFit_ClampsAndScalesByDiscCapacity()
    {
        // Very short video should clamp to MaxVideoBitrateKbps (8500)
        int shortDvd5 = DvdMediaProber.CalculateVideoBitrateKbps(
            TimeSpan.FromMinutes(5),
            DvdTargetDisc.Dvd5_4_7GB,
            DvdQualityPreset.BestFit,
            audioStreams: 1);
        Assert.Equal(DvdMediaProber.MaxVideoBitrateKbps, shortDvd5);

        // Very long video should clamp to MinVideoBitrateKbps (1500)
        int longDvd5 = DvdMediaProber.CalculateVideoBitrateKbps(
            TimeSpan.FromHours(12),
            DvdTargetDisc.Dvd5_4_7GB,
            DvdQualityPreset.BestFit,
            audioStreams: 1);
        Assert.Equal(DvdMediaProber.MinVideoBitrateKbps, longDvd5);

        // 2-hour movie fits at a higher bitrate on DVD-9 than DVD-5
        int twoHourDvd5 = DvdMediaProber.CalculateVideoBitrateKbps(
            TimeSpan.FromHours(2),
            DvdTargetDisc.Dvd5_4_7GB,
            DvdQualityPreset.BestFit,
            audioStreams: 1);
        int twoHourDvd9 = DvdMediaProber.CalculateVideoBitrateKbps(
            TimeSpan.FromHours(2),
            DvdTargetDisc.Dvd9_8_5GB,
            DvdQualityPreset.BestFit,
            audioStreams: 1);

        Assert.InRange(twoHourDvd5, DvdMediaProber.MinVideoBitrateKbps, DvdMediaProber.MaxVideoBitrateKbps);
        Assert.True(twoHourDvd9 > twoHourDvd5, $"Expected DVD-9 bitrate ({twoHourDvd9}) > DVD-5 ({twoHourDvd5}).");

        // Estimated size for BestFit on 2h DVD-5 should fit within Dvd5UsableBytes
        long estimated = DvdMediaProber.EstimateDiscBytes(TimeSpan.FromHours(2), twoHourDvd5, 192);
        Assert.True(estimated <= DvdMediaProber.Dvd5UsableBytes,
            $"BestFit estimated size {estimated} exceeded DVD-5 usable bytes {DvdMediaProber.Dvd5UsableBytes}.");
    }

    [Fact]
    public void CalculateVideoBitrateKbps_ManyAudioStreams_RespectsMaxTotalBitrate()
    {
        // 8 audio streams * 192 kbps = 1536 kbps; max total = 9400 -> video <= 7864 kbps
        int kbps = DvdMediaProber.CalculateVideoBitrateKbps(
            TimeSpan.FromMinutes(10),
            DvdTargetDisc.Dvd9_8_5GB,
            DvdQualityPreset.High,
            audioStreams: 8);

        Assert.True(kbps + 8 * DvdMediaProber.DefaultAudioBitrateKbps <= DvdMediaProber.MaxTotalBitrateKbps);
        Assert.Equal(DvdMediaProber.MaxTotalBitrateKbps - 8 * DvdMediaProber.DefaultAudioBitrateKbps, kbps);
    }

    [Theory]
    [InlineData(DvdAspectRatio.Widescreen16x9, 1.333, DvdAspectRatio.Widescreen16x9)]
    [InlineData(DvdAspectRatio.Standard4x3, 1.778, DvdAspectRatio.Standard4x3)]
    [InlineData(DvdAspectRatio.Auto, 1.778, DvdAspectRatio.Widescreen16x9)]
    [InlineData(DvdAspectRatio.Auto, 1.55, DvdAspectRatio.Widescreen16x9)]
    [InlineData(DvdAspectRatio.Auto, 1.54, DvdAspectRatio.Standard4x3)]
    [InlineData(DvdAspectRatio.Auto, 1.333, DvdAspectRatio.Standard4x3)]
    public void ResolveAspectRatio_ReturnsExpectedMode(
        DvdAspectRatio requested,
        double probedDar,
        DvdAspectRatio expected)
    {
        Assert.Equal(expected, DvdMediaProber.ResolveAspectRatio(requested, probedDar));
    }

    [Fact]
    public void EstimateDiscBytes_ZeroOrNegativeDuration_ReturnsZero()
    {
        Assert.Equal(0, DvdMediaProber.EstimateDiscBytes(TimeSpan.Zero, 6000, 192));
        Assert.Equal(0, DvdMediaProber.EstimateDiscBytes(TimeSpan.FromSeconds(-5), 6000, 192));
    }

    [Fact]
    public void EstimateDiscBytes_PositiveDuration_IncludesMuxOverhead()
    {
        // (6000 + 192) kbps * 1000 / 8 * 100s * 1.05
        long bytes = DvdMediaProber.EstimateDiscBytes(TimeSpan.FromSeconds(100), 6000, 192);
        long expected = (long)Math.Ceiling((6192.0 * 1000.0 / 8.0) * 100.0 * 1.05);
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData(DvdVideoStandard.Pal, DvdAspectRatio.Widescreen16x9, "1024:576", "720:576", "25", "15", "16:9", "64/45")]
    [InlineData(DvdVideoStandard.Pal, DvdAspectRatio.Standard4x3, "768:576", "720:576", "25", "15", "4:3", "16/15")]
    [InlineData(DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, "854:480", "720:480", "30000/1001", "18", "16:9", "32/27")]
    [InlineData(DvdVideoStandard.Ntsc, DvdAspectRatio.Standard4x3, "640:480", "720:480", "30000/1001", "18", "4:3", "8/9")]
    public void BuildFfmpegArguments_StandardAndAspect_ProducesCompliantArgs(
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        string expectedDisplayScale,
        string expectedScale,
        string expectedRate,
        string expectedGop,
        string expectedAspect,
        string expectedSar)
    {
        var source = new DvdSourceVideo(@"C:\Videos\clip.mp4");
        var info = new DvdMediaInfo(source.FilePath, TimeSpan.FromSeconds(30), 1280, 720, 16.0 / 9.0, 2, 0, "1280x720");
        var options = new DvdAuthoringOptions
        {
            Sources = [source],
            Standard = standard,
            AspectRatio = aspect,
            AudioSelection = DvdAudioSelection.FirstTrack,
            SubtitleMode = DvdSubtitleMode.None
        };

        string[] args = DvdMpeg2Encoder.BuildFfmpegArguments(source, info, options, 6000, aspect, @"C:\Temp\out.mpg");

        AssertArgPair(args, "-f", "dvd");
        AssertArgPair(args, "-c:v", "mpeg2video");
        AssertArgPair(args, "-b:v", "6000k");
        AssertArgPair(args, "-r", expectedRate);
        AssertArgPair(args, "-g", expectedGop);
        AssertArgPair(args, "-aspect", expectedAspect);
        AssertArgPair(args, "-c:a", "ac3");
        AssertArgPair(args, "-ar", "48000");
        AssertArgPair(args, "-b:a", "192k");
        AssertArgPair(args, "-map", "0:a:0?");

        string vf = GetArgValue(args, "-vf");
        Assert.Contains($"scale={expectedDisplayScale}:force_original_aspect_ratio=decrease", vf);
        Assert.Contains($"pad={expectedDisplayScale}:(ow-iw)/2:(oh-ih)/2", vf);
        Assert.Contains($"scale={expectedScale}", vf);
        Assert.Contains($"setsar={expectedSar}", vf);
        Assert.Equal(@"C:\Temp\out.mpg", args[^1]);
    }

    [Fact]
    public void BuildFfmpegArguments_AudioSelection_FirstTrackVsAllTracks()
    {
        var source = new DvdSourceVideo(@"C:\Videos\multi.mkv");
        var info3 = new DvdMediaInfo(source.FilePath, TimeSpan.FromMinutes(10), 1920, 1080, 16.0 / 9.0, 3, 0, "1920x1080");
        var info10 = new DvdMediaInfo(source.FilePath, TimeSpan.FromMinutes(10), 1920, 1080, 16.0 / 9.0, 10, 0, "1920x1080");

        var firstTrackOpts = new DvdAuthoringOptions
        {
            Sources = [source],
            AudioSelection = DvdAudioSelection.FirstTrack
        };
        string[] firstArgs = DvdMpeg2Encoder.BuildFfmpegArguments(source, info3, firstTrackOpts, 5500, DvdAspectRatio.Widescreen16x9, @"C:\Temp\t1.mpg");
        AssertArgPair(firstArgs, "-map", "0:a:0?");
        Assert.DoesNotContain("0:a?", firstArgs);

        var allTracksOpts = new DvdAuthoringOptions
        {
            Sources = [source],
            AudioSelection = DvdAudioSelection.AllTracks
        };
        string[] allArgs3 = DvdMpeg2Encoder.BuildFfmpegArguments(source, info3, allTracksOpts, 5500, DvdAspectRatio.Widescreen16x9, @"C:\Temp\t2.mpg");
        AssertArgPair(allArgs3, "-map", "0:a?");

        // > 8 audio tracks should cap at 8 explicit stream maps (0:a:0? .. 0:a:7?)
        string[] allArgs10 = DvdMpeg2Encoder.BuildFfmpegArguments(source, info10, allTracksOpts, 5500, DvdAspectRatio.Widescreen16x9, @"C:\Temp\t3.mpg");
        for (int i = 0; i < 8; i++)
            Assert.Contains($"0:a:{i}?", allArgs10);
        Assert.DoesNotContain("0:a:8?", allArgs10);
    }

    [Fact]
    public void BuildFfmpegArguments_ExternalSrt_EscapesSpecialCharacters()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"dvd_srt_test_[1]_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string srtPath = Path.Combine(tempDir, "bob's [sub].srt");

        try
        {
            File.WriteAllText(srtPath, "1\r\n00:00:00,000 --> 00:00:02,000\r\nHello\r\n");

            var source = new DvdSourceVideo(@"C:\Videos\movie.mp4", srtPath);
            var info = new DvdMediaInfo(source.FilePath, TimeSpan.FromSeconds(10), 1280, 720, 16.0 / 9.0, 1, 0, "1280x720");
            var options = new DvdAuthoringOptions
            {
                Sources = [source],
                Standard = DvdVideoStandard.Pal,
                SubtitleMode = DvdSubtitleMode.ExternalSrt
            };

            string[] args = DvdMpeg2Encoder.BuildFfmpegArguments(source, info, options, 5500, DvdAspectRatio.Widescreen16x9, @"C:\Temp\sub.mpg");
            string vf = GetArgValue(args, "-vf");

            Assert.Contains(",subtitles='", vf);
            Assert.DoesNotContain(@"\", vf.Replace(@"\:", "").Replace(@"\'", "").Replace(@"\[", "").Replace(@"\]", ""));
            Assert.Contains(@"\:", vf);
            Assert.Contains(@"\'", vf);
            Assert.Contains(@"\[", vf);
            Assert.Contains(@"\]", vf);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task EndToEnd_AuthorVideoTsAndSaveIso_ProducesFfprobeCompliantDvdVideo()
    {
        if (!DvdVideoConverter.IsAvailable)
            return;

        string ffmpeg = DvdVideoConverter.FindFfmpeg()!;
        string ffprobe = DvdVideoConverter.FindFfprobe()!;

        string tempRoot = Path.Combine(Path.GetTempPath(), $"cdb10_dvd_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            // 1. Synthesize a 3-second 720p H.264/AAC MP4 with FFmpeg.
            string sampleMp4 = Path.Combine(tempRoot, "sample_3s.mp4");
            var genResult = await RunProcessAsync(ffmpeg,
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=duration=3:size=1280x720:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast",
                "-c:a", "aac", "-b:a", "128k",
                "-shortest",
                sampleMp4
            ]);
            Assert.True(genResult.ExitCode == 0 && File.Exists(sampleMp4),
                $"Failed to synthesize sample MP4 (exit {genResult.ExitCode}): {genResult.Stderr}");

            // 2. Probe the generated MP4 with DvdMediaProber.
            DvdMediaInfo info = await DvdMediaProber.ProbeAsync(sampleMp4);
            Assert.Equal(1280, info.Width);
            Assert.Equal(720, info.Height);
            Assert.True(info.Duration.TotalSeconds >= 2.5);
            Assert.Equal(1, info.AudioStreamCount);

            // 3. Encode + Author VIDEO_TS directly via DvdMpeg2Encoder & VideoTsAuthorer to inspect files.
            string directMpg = Path.Combine(tempRoot, "direct.mpg");
            string directDiscRoot = Path.Combine(tempRoot, "direct_disc");
            var source = new DvdSourceVideo(sampleMp4);
            var options = new DvdAuthoringOptions
            {
                Sources = [source],
                TargetDisc = DvdTargetDisc.Dvd5_4_7GB,
                Standard = DvdVideoStandard.Pal,
                AspectRatio = DvdAspectRatio.Widescreen16x9,
                Quality = DvdQualityPreset.Medium,
                AudioSelection = DvdAudioSelection.FirstTrack,
                SubtitleMode = DvdSubtitleMode.None,
                VolumeName = "TEST_DVD",
                CreateMenu = true
            };

            await DvdMpeg2Encoder.EncodeTitleAsync(
                source,
                info,
                options,
                videoBitrateKbps: 5500,
                resolvedAspect: DvdAspectRatio.Widescreen16x9,
                outputMpgPath: directMpg);
            Assert.True(File.Exists(directMpg) && new FileInfo(directMpg).Length > 0);

            string videoTsDir = await VideoTsAuthorer.AuthorAsync(
                [new AuthoredTitleInput(directMpg, "Sample", info.Duration, DvdVideoStandard.Pal, DvdAspectRatio.Widescreen16x9, 1)],
                directDiscRoot,
                "TEST_DVD",
                createMenu: true);

            // Verify all mandatory DVD-Video files exist, are non-empty, 2048-byte aligned, and IFO == BUP.
            string[] expectedFiles =
            [
                "VIDEO_TS.IFO",
                "VIDEO_TS.BUP",
                "VTS_01_0.IFO",
                "VTS_01_0.BUP",
                "VTS_01_1.VOB"
            ];
            foreach (string file in expectedFiles)
            {
                string fullPath = Path.Combine(videoTsDir, file);
                Assert.True(File.Exists(fullPath), $"Missing authored DVD file: {file}");
                long len = new FileInfo(fullPath).Length;
                Assert.True(len > 0 && len % 2048 == 0, $"File {file} has invalid DVD sector size: {len} bytes.");
            }
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.IFO")),
                File.ReadAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.BUP")));
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(videoTsDir, "VTS_01_0.IFO")),
                File.ReadAllBytes(Path.Combine(videoTsDir, "VTS_01_0.BUP")));
            Assert.True(Directory.Exists(Path.Combine(directDiscRoot, "AUDIO_TS")));

            // Verify DvdVideoBurner.ValidateVideoTsFolder passes.
            var validation = DvdVideoBurner.ValidateVideoTsFolder(directDiscRoot);
            Assert.True(validation.IsValid, validation.Message);

            // Verify ffprobe -f dvdvideo on the authored VIDEO_TS folder.
            await AssertFfprobeDvdVideoValidAsync(ffprobe, directDiscRoot);

            // 4. Run full DvdFromFileAuthorer.SaveIsoAsync pipeline to produce a real DVD-Video .iso.
            string outputIso = Path.Combine(tempRoot, "test_dvd.iso");
            var authorer = new DvdFromFileAuthorer();
            var reportedPhases = new List<BurnPhase>();
            var progress = new Progress<BurnProgress>(p => reportedPhases.Add(p.Phase));

            await authorer.SaveIsoAsync(options, outputIso, progress);

            Assert.True(File.Exists(outputIso), "Expected DVD-Video ISO file to be created.");
            Assert.True(new FileInfo(outputIso).Length > 100_000, "Expected non-trivial DVD-Video ISO size.");

            // Verify ffprobe -f dvdvideo on the generated DVD-Video .iso image!
            await AssertFfprobeDvdVideoValidAsync(ffprobe, outputIso);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task AssertFfprobeDvdVideoValidAsync(string ffprobePath, string dvdInputPath)
    {
        var probe = await RunProcessAsync(ffprobePath,
        [
            "-v", "error",
            "-f", "dvdvideo",
            "-i", dvdInputPath,
            "-print_format", "json",
            "-show_streams",
            "-show_format"
        ]);

        Assert.True(probe.ExitCode == 0,
            $"ffprobe -f dvdvideo failed on '{dvdInputPath}' (exit {probe.ExitCode}): {probe.Stderr}");

        using var doc = JsonDocument.Parse(probe.Stdout);
        Assert.True(doc.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array,
            $"ffprobe output missing streams array for '{dvdInputPath}'.");

        bool hasPalMpeg2 = false;
        bool hasAc3 = false;

        foreach (var stream in streams.EnumerateArray())
        {
            string codecType = stream.TryGetProperty("codec_type", out var ctEl) ? ctEl.GetString() ?? "" : "";
            string codecName = stream.TryGetProperty("codec_name", out var cnEl) ? cnEl.GetString() ?? "" : "";

            if (codecType == "video" && codecName == "mpeg2video")
            {
                int width = stream.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 0;
                int height = stream.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 0;
                if (width == 720 && height == 576)
                    hasPalMpeg2 = true;
            }
            else if (codecType == "audio" && codecName == "ac3")
            {
                hasAc3 = true;
            }
        }

        Assert.True(hasPalMpeg2, $"Expected 720x576 PAL mpeg2video stream in '{dvdInputPath}'. JSON: {probe.Stdout}");
        Assert.True(hasAc3, $"Expected ac3 audio stream in '{dvdInputPath}'. JSON: {probe.Stdout}");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void AssertArgPair(string[] args, string flag, string expectedValue)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag && args[i + 1] == expectedValue)
                return;
        }
        Assert.Fail($"Expected argument pair '{flag} {expectedValue}' not found in: {string.Join(" ", args)}");
    }

    private static string GetArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag)
                return args[i + 1];
        }
        Assert.Fail($"Expected flag '{flag}' not found in: {string.Join(" ", args)}");
        return string.Empty;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
