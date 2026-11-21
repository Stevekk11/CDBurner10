using CDBurner10.Core.Settings;

namespace CDBurner10.Core.Audio;

public sealed class CrossfadingAudioConverter : IDisposable
{
    private const int CdSectorBytes = 2352;
    private int _trackIndex;
    private string _nextTrackFileName = string.Empty;
    private int _lastTrackStreamBegin;
    private int _lastTrackStreamEnd;
    private int _mixerStream;

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
        var results = new List<string>();

        int stream = _lastTrackStreamEnd != 0
            ? _lastTrackStreamEnd
            : OpenDecodeStream(fileName);

        if (stream == 0)
        {
            throw new InvalidOperationException($"Failed to open audio stream for '{fileName}' (BASS error {NativeBass.BASS_ErrorGetCode()}).");
        }

        int nextStream = !string.IsNullOrEmpty(_nextTrackFileName)
            ? OpenDecodeStream(_nextTrackFileName)
            : 0;

        if (TargetSampleRate <= 0)
        {
            if (NativeBass.BASS_ChannelGetInfo(stream, out var info) && info.freq > 0)
                TargetSampleRate = info.freq;
            else
                TargetSampleRate = 44100;
        }

        if (_mixerStream == 0)
        {
            _mixerStream = NativeBass.BASS_Mixer_StreamCreate(TargetSampleRate, 2, NativeBass.BASS_STREAM_DECODE);
        }

        if (_lastTrackStreamEnd == 0)
        {
            NativeBass.BASS_Mixer_StreamAddChannel(_mixerStream, stream, NativeBass.BASS_STREAM_DECODE);
        }

        double gainMultiplier = 1.0;
        if (ProcessReplayGain != ReplayGainMode.None)
        {
            var (trackDb, albumDb) = AudioTagReader.ReadReplayGain(fileName);
            double? db = ProcessReplayGain == ReplayGainMode.Track
                ? trackDb ?? albumDb
                : albumDb ?? trackDb;
            if (db.HasValue)
            {
                gainMultiplier = Math.Pow(10.0, db.Value / 20.0);
            }
        }

        WavWriter? writer = null;
        long totalBytes = NativeBass.BASS_ChannelGetLength(stream, NativeBass.BASS_POS_BYTE);
        double totalSeconds = totalBytes > 0 ? NativeBass.BASS_ChannelBytes2Seconds(stream, totalBytes) : 0;
        var buffer = new short[CdSectorBytes / 2 + 1];
        int lastPercent = -1;
        int splitIndex = splitPositions is { Length: > 0 } ? 0 : -1;
        bool crossfadeStarted = false;

        progress?.Report(0);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                double posSec = NativeBass.BASS_ChannelBytes2Seconds(
                    stream,
                    NativeBass.BASS_ChannelGetPosition(stream, NativeBass.BASS_POS_BYTE));

                if (crossfadeStarted && CrossfadeLength > TimeSpan.Zero &&
                    posSec >= totalSeconds - CrossfadeLength.TotalSeconds / 2.0)
                {
                    break;
                }

                int bytesRead = NativeBass.BASS_ChannelGetData(_mixerStream, buffer, CdSectorBytes);
                if (bytesRead <= 0) break;

                if (Math.Abs(gainMultiplier - 1.0) > 0.0001)
                {
                    ApplyGain(buffer, bytesRead / 2, gainMultiplier);
                }

                double afterSec = NativeBass.BASS_ChannelBytes2Seconds(
                    stream,
                    NativeBass.BASS_ChannelGetPosition(stream, NativeBass.BASS_POS_BYTE));

                if (!crossfadeStarted && CrossfadeLength > TimeSpan.Zero &&
                    nextStream != 0 && afterSec >= totalSeconds - CrossfadeLength.TotalSeconds)
                {
                    long fadeBytes = NativeBass.BASS_ChannelSeconds2Bytes(_mixerStream, CrossfadeLength.TotalSeconds);
                    NativeBass.BASS_Mixer_ChannelSetEnvelope(
                        stream,
                        NativeBass.BASS_MIXER_ENV_VOL,
                        [new(0, 1f), new(fadeBytes, 0f)],
                        2);
                    NativeBass.BASS_Mixer_StreamAddChannel(_mixerStream, nextStream, NativeBass.BASS_STREAM_DECODE);
                    NativeBass.BASS_Mixer_ChannelSetEnvelope(
                        nextStream,
                        NativeBass.BASS_MIXER_ENV_VOL,
                        [new(0, 0f), new(fadeBytes, 1f)],
                        2);
                    crossfadeStarted = true;
                }

                if (splitIndex >= 0 && splitPositions is not null)
                {
                    if (splitPositions[splitIndex] <= afterSec)
                    {
                        if (writer is not null)
                        {
                            WriteSilenceAndClose(writer, secondsPostGap);
                            results.Add(writer.FileName);
                        }
                        writer = CreateTempWriter(fileName, splitIndex + 1);
                        WriteSilence(writer, secondsPreGap);
                        splitIndex = splitIndex >= splitPositions.Length - 1 ? -1 : splitIndex + 1;
                    }
                }
                else if (writer is null)
                {
                    writer = CreateTempWriter(fileName, 1);
                    WriteSilence(writer, secondsPreGap);
                }

                writer?.WriteSamples(buffer, bytesRead);

                if (totalSeconds > 0)
                {
                    int pct = (int)Math.Clamp(afterSec / totalSeconds * 100.0, 0, 100);
                    if (pct != lastPercent)
                    {
                        lastPercent = pct;
                        progress?.Report(pct);
                    }
                }
            }

            if (writer is not null)
            {
                WriteSilenceAndClose(writer, secondsPostGap);
                results.Add(writer.FileName);
                writer = null;
            }
        }
        finally
        {
            writer?.Dispose();
        }

        progress?.Report(100);

        if (_lastTrackStreamBegin != 0)
        {
            NativeBass.BASS_StreamFree(_lastTrackStreamBegin);
        }
        _lastTrackStreamBegin = stream;
        _lastTrackStreamEnd = nextStream;

        if (string.IsNullOrEmpty(_nextTrackFileName))
        {
            NativeBass.BASS_StreamFree(stream);
            _lastTrackStreamBegin = 0;
            if (_mixerStream != 0)
            {
                NativeBass.BASS_StreamFree(_mixerStream);
                _mixerStream = 0;
            }
        }

        return results.ToArray();
    }

    private int OpenDecodeStream(string filePath)
    {
        if (filePath.StartsWith("MixedMode:", StringComparison.OrdinalIgnoreCase))
        {
            var payload = filePath["MixedMode:".Length..];
            var parts = payload.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int driveIdx) && int.TryParse(parts[1], out int trackIdx))
            {
                return NativeBass.BASS_CD_StreamCreate(driveIdx, trackIdx, NativeBass.BASS_STREAM_DECODE);
            }
        }

        return NativeBass.BASS_StreamCreateFile(false, filePath, 0, 0, NativeBass.BASS_STREAM_DECODE | NativeBass.BASS_UNICODE);
    }

    private WavWriter CreateTempWriter(string sourceFile, int part)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceFile);
        var path = SettingsService.Default.GetTempRipFileName($"{baseName}-{part}", ".wav");
        return new WavWriter(path, TargetSampleRate, 2, 16);
    }

    private void WriteSilence(WavWriter writer, float seconds)
    {
        if (seconds <= 0f) return;
        int bytes = (int)(TargetSampleRate * 4 * seconds);
        writer.WriteSilence(bytes);
    }

    private void WriteSilenceAndClose(WavWriter writer, float secondsPostGap)
    {
        WriteSilence(writer, secondsPostGap);
        writer.Close();
    }

    private static void ApplyGain(short[] samples, int count, double gain)
    {
        for (int i = 0; i < count; i++)
        {
            double scaled = samples[i] * gain;
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            else if (scaled < short.MinValue) scaled = short.MinValue;
            samples[i] = (short)scaled;
        }
    }

    public void Dispose()
    {
        if (_lastTrackStreamBegin != 0)
        {
            NativeBass.BASS_StreamFree(_lastTrackStreamBegin);
            _lastTrackStreamBegin = 0;
        }
        if (_lastTrackStreamEnd != 0)
        {
            NativeBass.BASS_StreamFree(_lastTrackStreamEnd);
            _lastTrackStreamEnd = 0;
        }
        if (_mixerStream != 0)
        {
            NativeBass.BASS_StreamFree(_mixerStream);
            _mixerStream = 0;
        }
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
