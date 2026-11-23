using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CDBurner10.Core.Dvd;
using CDBurner10.Core.Settings;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CDBurner10.Core.Audio;

/// <summary>
/// Converts audio files (and CD-DA tracks) into 44.1 kHz 16-bit stereo PCM WAV files
/// with optional ReplayGain, pre/post-gap silence, CUE splitting, and linear crossfading
/// between consecutive tracks — powered by NAudio, FFmpeg, and Win32 CD-DA IOCTLs
/// (replaces closed-source BASS / bassmix).
/// </summary>
public sealed class CrossfadingAudioConverter : IDisposable
{
    private const int BytesPerSample = 2;
    private const int Channels = 2;
    private const int BytesPerFrame = BytesPerSample * Channels; // 4 bytes per stereo 16-bit frame
    private const int CdSectorBytes = 2352;

    private int _trackIndex;
    private string _nextTrackFileName = string.Empty;
    private short[]? _cachedNextSamples;
    private short[]? _pendingCrossfadeTail;
    private int _nextStartSampleOffset;

    public ReplayGainMode ProcessReplayGain { get; set; } = ReplayGainMode.None;
    public string[] Items { get; set; } = [];
    public TimeSpan CrossfadeLength { get; set; } = TimeSpan.Zero;
    public int TargetSampleRate { get; set; } = 44100;

    public string NextToWav(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string fileName = Items[_trackIndex];
        _nextTrackFileName = _trackIndex >= Items.Length - 1 ? string.Empty : Items[_trackIndex + 1];
        _trackIndex++;
        var outputs = ToWav(fileName, null, 0f, 0f, progress, ct);
        return outputs[0];
    }

    public string ToWav(
        string fileName,
        float secondsPreGap = 0f,
        float secondsPostGap = 0f,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var outputs = ToWav(fileName, null, secondsPreGap, secondsPostGap, progress, ct);
        return outputs[0];
    }

    public string[] ToWav(
        string fileName,
        double[]? splitPositions,
        float secondsPreGap,
        float secondsPostGap,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        BassAudioEngine.Initialize();
        if (TargetSampleRate <= 0)
            TargetSampleRate = 44100;

        progress?.Report(0);

        // Decode current track (or reuse pre-decoded next track from previous crossfade)
        short[] currentSamples;
        int startSampleOffset;
        if (_cachedNextSamples is not null)
        {
            currentSamples = _cachedNextSamples;
            startSampleOffset = _nextStartSampleOffset;
            _cachedNextSamples = null;
            _nextStartSampleOffset = 0;
        }
        else
        {
            currentSamples = DecodeToStereo16Pcm(fileName, ct);
            ApplyReplayGainIfEnabled(fileName, currentSamples);
            startSampleOffset = 0;
        }

        // Handle CUE splitting (no crossfading when splitting a single file)
        if (splitPositions is { Length: > 0 })
        {
            var splitResults = WriteSplitFiles(fileName, currentSamples, splitPositions, secondsPreGap, secondsPostGap, progress, ct);
            progress?.Report(100);
            return splitResults;
        }

        short[]? crossfadeFirstHalf = null;
        int endSampleExclusive = currentSamples.Length;

        // Prepare crossfade overlap with the upcoming track if configured
        if (CrossfadeLength > TimeSpan.Zero && !string.IsNullOrEmpty(_nextTrackFileName))
        {
            var nextSamples = DecodeToStereo16Pcm(_nextTrackFileName, ct);
            ApplyReplayGainIfEnabled(_nextTrackFileName, nextSamples);

            int currentRemainingFrames = Math.Max(0, (currentSamples.Length - startSampleOffset) / Channels);
            int nextFrames = nextSamples.Length / Channels;
            int requestedFadeFrames = (int)(CrossfadeLength.TotalSeconds * TargetSampleRate);
            int fadeFrames = Math.Min(requestedFadeFrames, Math.Min(currentRemainingFrames / 2, nextFrames / 2));

            if (fadeFrames > 0)
            {
                int fadeSamples = fadeFrames * Channels;
                int fadeStartInCurrent = currentSamples.Length - fadeSamples;
                var mixedFade = new short[fadeSamples];

                for (int frame = 0; frame < fadeFrames; frame++)
                {
                    double t = (double)frame / fadeFrames;
                    double outGain = 1.0 - t;
                    double inGain = t;
                    int sIdx = frame * Channels;

                    for (int ch = 0; ch < Channels; ch++)
                    {
                        double mixed = currentSamples[fadeStartInCurrent + sIdx + ch] * outGain
                                     + nextSamples[sIdx + ch] * inGain;
                        mixedFade[sIdx + ch] = ClampToInt16(mixed);
                    }
                }

                int midFrames = fadeFrames / 2;
                int midSamples = midFrames * Channels;
                crossfadeFirstHalf = mixedFade[..midSamples];
                _pendingCrossfadeTail = mixedFade[midSamples..];
                _cachedNextSamples = nextSamples;
                _nextStartSampleOffset = fadeSamples;
                endSampleExclusive = fadeStartInCurrent;
            }
            else
            {
                _cachedNextSamples = nextSamples;
                _nextStartSampleOffset = 0;
            }
        }

        var results = new List<string>();
        WavWriter? writer = null;
        try
        {
            writer = CreateTempWriter(fileName, 1);
            WriteSilence(writer, secondsPreGap);

            // Prepend second half of previous crossfade if any
            if (_pendingCrossfadeTail is not null && startSampleOffset > 0)
            {
                WriteShortArrayChunked(writer, _pendingCrossfadeTail, 0, _pendingCrossfadeTail.Length, ct);
                _pendingCrossfadeTail = null;
            }

            // Write main body of current track
            int bodyLength = Math.Max(0, endSampleExclusive - startSampleOffset);
            WriteShortArrayChunked(writer, currentSamples, startSampleOffset, bodyLength, ct, progress,
                crossfadeFirstHalf is not null ? 90.0 : 100.0);

            // Append first half of outgoing crossfade
            if (crossfadeFirstHalf is not null)
            {
                WriteShortArrayChunked(writer, crossfadeFirstHalf, 0, crossfadeFirstHalf.Length, ct);
            }

            WriteSilenceAndClose(writer, secondsPostGap);
            results.Add(writer.FileName);
            writer = null;
        }
        finally
        {
            writer?.Dispose();
        }

        if (string.IsNullOrEmpty(_nextTrackFileName))
        {
            _cachedNextSamples = null;
            _pendingCrossfadeTail = null;
            _nextStartSampleOffset = 0;
        }

        progress?.Report(100);
        return results.ToArray();
    }

    private string[] WriteSplitFiles(
        string fileName,
        short[] samples,
        double[] splitPositions,
        float secondsPreGap,
        float secondsPostGap,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var results = new List<string>();
        int totalFrames = samples.Length / Channels;
        var sorted = splitPositions.OrderBy(p => p).ToArray();

        for (int i = 0; i < sorted.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            int startFrame = (int)Math.Clamp(sorted[i] * TargetSampleRate, 0, totalFrames);
            int endFrame = i + 1 < sorted.Length
                ? (int)Math.Clamp(sorted[i + 1] * TargetSampleRate, startFrame, totalFrames)
                : totalFrames;

            int startSample = startFrame * Channels;
            int sampleCount = Math.Max(0, (endFrame - startFrame) * Channels);

            using var writer = CreateTempWriter(fileName, i + 1);
            WriteSilence(writer, secondsPreGap);
            WriteShortArrayChunked(writer, samples, startSample, sampleCount, ct);
            WriteSilenceAndClose(writer, secondsPostGap);
            results.Add(writer.FileName);

            progress?.Report((i + 1) * 100.0 / sorted.Length);
        }

        return results.ToArray();
    }

    private short[] DecodeToStereo16Pcm(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. MixedMode:driveIdx,trackIdx or .cda audio CD tracks -> Win32 IOCTL_CDROM_RAW_READ
        if (filePath.StartsWith("MixedMode:", StringComparison.OrdinalIgnoreCase))
        {
            var payload = filePath["MixedMode:".Length..];
            var parts = payload.Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int driveIdx) &&
                int.TryParse(parts[1], out int trackIdx))
            {
                var letter = CdAudioDevice.GetDriveLetterByIndex(driveIdx)
                             ?? throw new InvalidOperationException($"CD drive index {driveIdx} not found.");
                using var ms = new MemoryStream();
                CdAudioDevice.ReadAudioTrackPcm(letter, trackIdx + 1, ms, ct: ct);
                return BytesToShorts(ms.ToArray());
            }
        }

        if (PlaylistParser.IsCda(filePath))
        {
            var root = Path.GetPathRoot(filePath);
            var nameNoExt = Path.GetFileNameWithoutExtension(filePath);
            var digits = new string(nameNoExt.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(root) && int.TryParse(digits, out int trackNum))
            {
                using var ms = new MemoryStream();
                CdAudioDevice.ReadAudioTrackPcm(root, trackNum, ms, ct: ct);
                return BytesToShorts(ms.ToArray());
            }
        }

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Audio file not found: '{filePath}'", filePath);

        // 2. Fast path: already 16-bit stereo PCM WAV at TargetSampleRate
        if (filePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var wav = new WaveFileReader(filePath);
                if (wav.WaveFormat.Encoding == WaveFormatEncoding.Pcm &&
                    wav.WaveFormat.SampleRate == TargetSampleRate &&
                    wav.WaveFormat.BitsPerSample == 16 &&
                    wav.WaveFormat.Channels == 2)
                {
                    using var ms = new MemoryStream((int)Math.Min(wav.Length, int.MaxValue));
                    wav.CopyTo(ms);
                    return BytesToShorts(ms.ToArray());
                }
            }
            catch
            {
                // Fall through to FFmpeg / NAudio
            }
        }

        // 3. FFmpeg decode (handles FLAC, MP3, OGG, Opus, WMA, WavPack, ALAC, APE, MPC, AAC, M4A, AIFF, etc.)
        string? ffmpeg = DvdVideoConverter.FindFfmpeg();
        if (!string.IsNullOrWhiteSpace(ffmpeg))
        {
            return DecodeViaFfmpeg(ffmpeg, filePath, TargetSampleRate, ct);
        }

        // 4. Pure NAudio fallback (WAV, MP3, WMA, M4A, AAC, AIFF via Windows Media Foundation)
        return DecodeViaNAudio(filePath, TargetSampleRate, ct);
    }

    private static short[] DecodeViaFfmpeg(string ffmpegPath, string filePath, int sampleRate, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("pipe:1");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start ffmpeg to decode '{filePath}'.");

        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        using var ms = new MemoryStream();
        var errTask = process.StandardError.ReadToEndAsync(ct);
        process.StandardOutput.BaseStream.CopyTo(ms);
        process.WaitForExit();
        string stderr = errTask.GetAwaiter().GetResult();

        ct.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Failed to decode audio file '{filePath}': {(string.IsNullOrWhiteSpace(stderr) ? $"ffmpeg exit {process.ExitCode}" : stderr.Trim())}");

        return BytesToShorts(ms.ToArray());
    }

    private static short[] DecodeViaNAudio(string filePath, int targetSampleRate, CancellationToken ct)
    {
        using var reader = new AudioFileReader(filePath);
        ISampleProvider provider = reader;

        if (provider.WaveFormat.SampleRate != targetSampleRate)
            provider = new WdlResamplingSampleProvider(provider, targetSampleRate);

        if (provider.WaveFormat.Channels == 1)
            provider = new MonoToStereoSampleProvider(provider);

        var outSamples = new List<short>();
        var floatBuf = new float[targetSampleRate * Channels];
        int read;
        while ((read = provider.Read(floatBuf, 0, floatBuf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
            {
                outSamples.Add(ClampToInt16(floatBuf[i] * short.MaxValue));
            }
        }
        return outSamples.ToArray();
    }

    private void ApplyReplayGainIfEnabled(string fileName, short[] samples)
    {
        if (ProcessReplayGain == ReplayGainMode.None || samples.Length == 0)
            return;

        var (trackDb, albumDb) = AudioTagReader.ReadReplayGain(fileName);
        double? db = ProcessReplayGain == ReplayGainMode.Track
            ? trackDb ?? albumDb
            : albumDb ?? trackDb;

        if (!db.HasValue) return;

        double gain = Math.Pow(10.0, db.Value / 20.0);
        if (Math.Abs(gain - 1.0) <= 0.0001) return;

        for (int i = 0; i < samples.Length; i++)
            samples[i] = ClampToInt16(samples[i] * gain);
    }

    private static void WriteShortArrayChunked(
        WavWriter writer,
        short[] samples,
        int offsetSamples,
        int countSamples,
        CancellationToken ct,
        IProgress<double>? progress = null,
        double maxProgressPercent = 100.0)
    {
        if (countSamples <= 0) return;

        int chunkShorts = CdSectorBytes / 2;
        var chunk = new short[chunkShorts];
        int written = 0;
        int lastPct = -1;

        while (written < countSamples)
        {
            ct.ThrowIfCancellationRequested();
            int take = Math.Min(chunkShorts, countSamples - written);
            Array.Copy(samples, offsetSamples + written, chunk, 0, take);
            writer.WriteSamples(chunk, take * BytesPerSample);
            written += take;

            if (progress is not null)
            {
                int pct = (int)Math.Clamp((double)written / countSamples * maxProgressPercent, 0, maxProgressPercent);
                if (pct != lastPct)
                {
                    lastPct = pct;
                    progress.Report(pct);
                }
            }
        }
    }

    private static short[] BytesToShorts(byte[] bytes)
    {
        int count = bytes.Length / 2;
        var shorts = new short[count];
        MemoryMarshal.Cast<byte, short>(bytes.AsSpan(0, count * 2)).CopyTo(shorts);
        return shorts;
    }

    private static short ClampToInt16(double value)
    {
        if (value >= short.MaxValue) return short.MaxValue;
        if (value <= short.MinValue) return short.MinValue;
        return (short)Math.Round(value);
    }

    private WavWriter CreateTempWriter(string sourceFile, int part)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceFile);
        var path = SettingsService.Default.GetTempRipFileName($"{baseName}-{part}", ".wav");
        return new WavWriter(path, TargetSampleRate, Channels, 16);
    }

    private void WriteSilence(WavWriter writer, float seconds)
    {
        if (seconds <= 0f) return;
        int bytes = (int)(TargetSampleRate * BytesPerFrame * seconds);
        writer.WriteSilence(bytes);
    }

    private void WriteSilenceAndClose(WavWriter writer, float secondsPostGap)
    {
        WriteSilence(writer, secondsPostGap);
        writer.Close();
    }

    public void Dispose()
    {
        _cachedNextSamples = null;
        _pendingCrossfadeTail = null;
        _nextStartSampleOffset = 0;
        GC.SuppressFinalize(this);
    }
}

public static class AudioConverter
{
    public static Task<string> ConvertToWavAsync(
        string sourceFile,
        ReplayGainMode replayGain = ReplayGainMode.None,
        float preGapSeconds = 0f,
        float postGapSeconds = 0f,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var converter = new CrossfadingAudioConverter
            {
                TargetSampleRate = 44100,
                ProcessReplayGain = replayGain
            };
            return converter.ToWav(sourceFile, preGapSeconds, postGapSeconds, progress, ct);
        }, ct);
    }

    public static Task<string[]> SplitAudioByCueAsync(
        string audioFile,
        double[] splitPositionsSeconds,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var converter = new CrossfadingAudioConverter
            {
                TargetSampleRate = 44100
            };
            return converter.ToWav(audioFile, splitPositionsSeconds, 0f, 0f, progress, ct);
        }, ct);
    }
}
