using System.Runtime.InteropServices;
using CDBurner10.Core.Audio;
using CDBurner10.Core.Native;
using CDBurner10.Core.Settings;
using CDBurner10.Core.Utilities;

namespace CDBurner10.Core.Burning;

public enum AudioGapMode
{
    TaoPause,
    DaoGapless,
    CustomGaps,
    Crossfade
}

public sealed class AudioBurnOptions
{
    public required DriveModel Drive { get; init; }
    public int WriteSpeedKBps { get; init; } = 0;
    public AudioGapMode GapMode { get; init; } = AudioGapMode.TaoPause;
    public int PauseSeconds { get; init; } = 2;
    public double CrossfadeSeconds { get; init; } = 0;
    public bool EnableCdText { get; init; } = true;
    public ReplayGainMode ReplayGainMode { get; init; } = ReplayGainMode.None;
    public bool FinalizeDisc { get; init; } = true;
    public bool TestBurn { get; init; } = false;
    public bool EjectAfterBurn { get; init; } = true;
    public int Copies { get; init; } = 1;
}

public sealed class AudioBurner
{
    private IDiscFormat2TrackAtOnce? _activeTao;
    private bool _isCancelled;

    public bool IsCancelled => _isCancelled;

    public Task BurnAsync(
        AudioCompilation compilation,
        AudioBurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            _isCancelled = false;
            using var reg = ct.Register(Cancel);

            if (compilation.Tracks.Count == 0)
                throw new InvalidOperationException("No audio tracks in compilation.");

            var tempFiles = new List<string>();
            try
            {
                BassAudioEngine.Initialize();

                // Prepare tracks (convert to 44.1kHz 16-bit stereo WAV if needed)
                var preparedPaths = PrepareTracks(compilation, options, tempFiles, progress, ct);

                int copies = Math.Max(1, options.Copies);
                for (int copy = 1; copy <= copies; copy++)
                {
                    ct.ThrowIfCancellationRequested();

                    var currentDisc = options.Drive.GetDiscInfo(refresh: false);
                    bool useDao = options.GapMode != AudioGapMode.TaoPause &&
                                  !string.IsNullOrWhiteSpace(options.Drive.Letter) &&
                                  (currentDisc.IsBlank || !currentDisc.HasDisc);

                    if (useDao)
                    {
                        var daoTracks = new List<ScsiOpticalDevice.DaoAudioTrack>(preparedPaths.Count);
                        for (int i = 0; i < preparedPaths.Count; i++)
                        {
                            var src = compilation.Tracks[i];
                            string title = AudioTrackItem.StripToCdTextSize(
                                string.IsNullOrWhiteSpace(src.CDTextTitle) ? src.Title : src.CDTextTitle);
                            string artist = AudioTrackItem.StripToCdTextSize(
                                string.IsNullOrWhiteSpace(src.CDTextArtist) ? src.Artist : src.CDTextArtist);
                            daoTracks.Add(new ScsiOpticalDevice.DaoAudioTrack(preparedPaths[i], title, artist, PreGapSectors: 0));
                        }

                        try
                        {
                            ScsiOpticalDevice.BurnAudioDao(
                                options.Drive.Letter,
                                daoTracks,
                                AudioTrackItem.StripToCdTextSize(compilation.Title),
                                AudioTrackItem.StripToCdTextSize(compilation.Artist),
                                options.EnableCdText,
                                options.TestBurn,
                                options.WriteSpeedKBps,
                                progress,
                                ct);
                        }
                        catch (IOException) when (!ct.IsCancellationRequested)
                        {
                            // Fallback to IMAPI2 TAO if drive does not support SAO cue sheet
                            BurnTaoImapi2(preparedPaths, options, progress, ct);
                        }
                    }
                    else
                    {
                        BurnTaoImapi2(preparedPaths, options, progress, ct);
                    }

                    if (options.EjectAfterBurn && !_isCancelled)
                    {
                        try { options.Drive.Eject(); } catch { }
                    }
                }

                progress?.Report(new BurnProgress(
                    _isCancelled ? BurnPhase.Cancelled : BurnPhase.Completed,
                    100,
                    Message: _isCancelled ? "Audio burn cancelled." : "Audio CD burned successfully."));
            }
            finally
            {
                foreach (var tmp in tempFiles)
                {
                    PathHelper.TryDeleteFile(tmp);
                }
            }
        }, ct);
    }

    private void BurnTaoImapi2(
        List<string> wavPaths,
        AudioBurnOptions options,
        IProgress<BurnProgress>? progress,
        CancellationToken ct)
    {
        IDiscRecorder2? recorder = null;
        IDiscFormat2TrackAtOnce? tao = null;
        bool mediaPrepared = false;

        try
        {
            recorder = options.Drive.OpenRecorder();
            tao = Imapi2Interop.CreateDiscFormat2TrackAtOnce();
            if (!tao.IsRecorderSupported(recorder))
                throw new InvalidOperationException($"Drive '{options.Drive.DisplayName}' does not support Audio CD burning.");

            if (!tao.IsCurrentMediaSupported(recorder))
            {
                // IMAPI2 TrackAtOnce only supports blank CD-R/CD-RW discs.
                // If an open multi-session CD-R/CD-RW is inserted, fall back to direct SPTI MMC TAO burning!
                var discInfo = options.Drive.GetDiscInfo(refresh: false);
                if (discInfo.HasDisc && discInfo.IsCd && (discInfo.IsNextSessionAllowed || discInfo.FreeSizeBytes > 0) && !string.IsNullOrWhiteSpace(options.Drive.Letter))
                {
                    Imapi2Interop.SafeRelease(tao);
                    tao = null;
                    Imapi2Interop.SafeRelease(recorder);
                    recorder = null;

                    ScsiOpticalDevice.BurnAudioTao(
                        options.Drive.Letter,
                        wavPaths,
                        options.FinalizeDisc,
                        options.TestBurn,
                        options.WriteSpeedKBps,
                        progress,
                        ct);
                    return;
                }

                throw new InvalidOperationException("No writable CD-R/CD-RW disc detected in the selected drive.");
            }

            tao.put_Recorder(recorder);
            tao.put_ClientName("CDBurner10");

            if (options.TestBurn)
            {
                progress?.Report(new BurnProgress(BurnPhase.Completed, 100, Message: "Audio CD test burn validated."));
                return;
            }

            int totalTracks = wavPaths.Count;
            int currentTrackIdx = 0;

            var sink = new DiscFormat2TrackAtOnceEventSink(args =>
            {
                int bufPct = args.TotalSystemBuffer > 0
                    ? (int)Math.Clamp(args.UsedSystemBuffer * 100.0 / args.TotalSystemBuffer, 0, 100)
                    : 100;

                int trackPct = args.SectorCount > 0
                    ? (int)Math.Clamp((args.LastWrittenLba - args.StartLba) * 100.0 / args.SectorCount, 0, 100)
                    : 0;
                int overall = totalTracks > 0
                    ? (int)Math.Clamp((currentTrackIdx * 100.0 + trackPct) / totalTracks, 0, 100)
                    : trackPct;

                progress?.Report(new BurnProgress(
                    BurnPhase.Burning,
                    overall,
                    bufPct,
                    TimeSpan.FromSeconds(Math.Max(0, args.RemainingTime)),
                    currentTrackIdx + 1,
                    totalTracks,
                    $"Burning audio track {currentTrackIdx + 1}/{totalTracks}... {overall}%"));
            });

            _activeTao = tao;
            using (Imapi2Interop.AdviseEventSink(tao, typeof(DDiscFormat2TrackAtOnceEvents).GUID, sink))
            {
                tao.PrepareMedia();
                mediaPrepared = true;

                // IMAPI2 TrackAtOnce requires PrepareMedia() before setting BUF, DoNotFinalizeMedia, or write speed
                try { tao.put_BufferUnderrunFreeDisabled(false); } catch (COMException) { }
                tao.put_DoNotFinalizeMedia(!options.FinalizeDisc);
                if (options.WriteSpeedKBps > 0)
                {
                    try { tao.SetWriteSpeed(Math.Max(1, options.WriteSpeedKBps / 2), false); } catch (COMException) { }
                }

                for (int i = 0; i < wavPaths.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    currentTrackIdx = i;

                    var (dataOffset, dataLength) = ScsiOpticalDevice.ReadWavPcmBounds(wavPaths[i]);
                    using var fs = new FileStream(wavPaths[i], FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var pcmSlice = new PcmSliceStream(fs, dataOffset, dataLength);
                    using var comStream = new ManagedComStream(pcmSlice, leaveOpen: true);

                    tao.AddAudioTrack(comStream);
                }

                progress?.Report(new BurnProgress(BurnPhase.ClosingSession, 99, 100, Message: "Closing audio session..."));
                tao.ReleaseMedia();
                mediaPrepared = false;
            }
        }
        finally
        {
            _activeTao = null;
            if (mediaPrepared && tao is not null)
            {
                try { tao.ReleaseMedia(); } catch { }
            }
            Imapi2Interop.SafeRelease(tao);
            Imapi2Interop.SafeRelease(recorder);
        }
    }

    private static List<string> PrepareTracks(
        AudioCompilation compilation,
        AudioBurnOptions options,
        List<string> tempFiles,
        IProgress<BurnProgress>? progress,
        CancellationToken ct)
    {
        var result = new List<string>(compilation.Tracks.Count);
        bool useCrossfade = options.GapMode == AudioGapMode.Crossfade && options.CrossfadeSeconds > 0;
        bool useCustomGaps = options.GapMode == AudioGapMode.CustomGaps;

        using var converter = new CrossfadingAudioConverter
        {
            TargetSampleRate = 44100,
            ProcessReplayGain = options.ReplayGainMode,
            CrossfadeLength = useCrossfade ? TimeSpan.FromSeconds(options.CrossfadeSeconds) : TimeSpan.Zero,
            Items = compilation.Tracks.Select(t => t.FileName).ToArray()
        };

        for (int i = 0; i < compilation.Tracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var track = compilation.Tracks[i];

            bool needsConversion =
                useCrossfade ||
                options.ReplayGainMode != ReplayGainMode.None ||
                (useCustomGaps && (track.PreGap > TimeSpan.Zero || track.PostGap > TimeSpan.Zero)) ||
                track.IsCda ||
                !Path.GetExtension(track.FileName).Equals(".wav", StringComparison.OrdinalIgnoreCase);

            if (!needsConversion)
            {
                result.Add(track.FileName);
                continue;
            }

            int trackIdx = i;
            var trackProg = new Progress<double>(p =>
            {
                int overall = (int)((trackIdx * 100.0 + p) / compilation.Tracks.Count);
                progress?.Report(new BurnProgress(
                    BurnPhase.Converting,
                    overall,
                    CurrentTrack: trackIdx + 1,
                    TotalTracks: compilation.Tracks.Count,
                    Message: $"Converting track {trackIdx + 1}/{compilation.Tracks.Count}: {track.Title}"));
            });

            string wavPath = useCrossfade
                ? converter.NextToWav(trackProg, ct)
                : converter.ToWav(
                    track.FileName,
                    useCustomGaps ? (float)track.PreGap.TotalSeconds : 0f,
                    useCustomGaps ? (float)track.PostGap.TotalSeconds : 0f,
                    trackProg,
                    ct);

            tempFiles.Add(wavPath);
            result.Add(wavPath);
        }

        return result;
    }

    public void Cancel()
    {
        _isCancelled = true;
        var tao = _activeTao;
        if (tao is not null)
        {
            _ = Task.Run(() =>
            {
                try { tao.CancelAddTrack(); } catch { }
            });
        }
    }

    /// <summary>
    /// Exposes the raw 44.1kHz 16-bit stereo PCM payload of a WAV file padded to 2352-byte CD-DA sector boundaries.
    /// </summary>
    private sealed class PcmSliceStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _dataLength;
        private readonly long _paddedLength;
        private long _position;

        public PcmSliceStream(Stream inner, long start, long dataLength)
        {
            _inner = inner;
            _start = start;
            _dataLength = dataLength;
            long rem = dataLength % ScsiOpticalDevice.AudioSectorSize;
            _paddedLength = rem == 0 ? dataLength : dataLength + (ScsiOpticalDevice.AudioSectorSize - rem);
            _inner.Seek(_start, SeekOrigin.Begin);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _paddedLength;
        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _paddedLength) return 0;
            int toProvide = (int)Math.Min(count, _paddedLength - _position);
            int totalRead = 0;

            if (_position < _dataLength)
            {
                int fromFile = (int)Math.Min(toProvide, _dataLength - _position);
                _inner.Seek(_start + _position, SeekOrigin.Begin);
                int r = _inner.Read(buffer, offset, fromFile);
                _position += r;
                totalRead += r;
                offset += r;
                toProvide -= r;
            }

            if (toProvide > 0 && _position >= _dataLength)
            {
                Array.Clear(buffer, offset, toProvide);
                _position += toProvide;
                totalRead += toProvide;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _paddedLength + offset,
                _ => _position
            };
            _position = Math.Clamp(target, 0, _paddedLength);
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
