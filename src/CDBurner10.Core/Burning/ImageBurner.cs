using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CDBurner10.Core.Native;

namespace CDBurner10.Core.Burning;

public sealed class ImageBurnOptions
{
    public required DriveModel Drive { get; init; }
    public required string ImageFileName { get; init; }
    public int WriteSpeedKBps { get; init; } = 0;
    public WriteMode Mode { get; init; } = WriteMode.Auto;
    public bool IsXaCdRom { get; init; } = false;
    public bool Padding { get; init; } = false;
    public bool FinalizeDisc { get; init; } = true;
    public bool VerifyData { get; init; } = false;
    public bool TestBurn { get; init; } = false;
    public bool EjectAfterBurn { get; init; } = true;
    public int Copies { get; init; } = 1;
}

public sealed class ImageBurner
{
    private IDiscFormat2Data? _activeFormat;
    private AudioBurner? _currentAudioBurner;
    private bool _isCancelled;

    public bool IsCancelled => _isCancelled;

    public Task BurnAsync(
        ImageBurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(options.ImageFileName))
                throw new FileNotFoundException($"Image file not found: '{options.ImageFileName}'", options.ImageFileName);

            _isCancelled = false;
            using var reg = ct.Register(Cancel);

            int copies = Math.Max(1, options.Copies);
            for (int copy = 1; copy <= copies; copy++)
            {
                ct.ThrowIfCancellationRequested();
                BurnIsoWithImapi2(options, progress, ct);

                if (options.EjectAfterBurn && !_isCancelled)
                {
                    try { options.Drive.Eject(); } catch { }
                }
            }

            progress?.Report(new BurnProgress(
                _isCancelled ? BurnPhase.Cancelled : BurnPhase.Completed,
                100,
                Message: _isCancelled ? "Image burn cancelled." : "Image burned successfully."));
        }, ct);
    }

    private void BurnIsoWithImapi2(
        ImageBurnOptions options,
        IProgress<BurnProgress>? progress,
        CancellationToken ct)
    {
        IDiscRecorder2? recorder = null;
        IDiscFormat2Data? format = null;
        IStream? imageStream = null;

        try
        {
            recorder = options.Drive.OpenRecorder();
            format = Imapi2Interop.CreateDiscFormat2Data();
            if (!format.IsRecorderSupported(recorder))
                throw new InvalidOperationException($"Drive '{options.Drive.DisplayName}' does not support image burning.");
            if (!format.IsCurrentMediaSupported(recorder))
                throw new InvalidOperationException("No writable disc detected in the selected drive.");

            format.put_Recorder(recorder);
            format.put_ClientName("CDBurner10");
            format.put_ForceMediaToBeClosed(options.FinalizeDisc);
            format.put_BufferUnderrunFreeDisabled(false);

            if (options.WriteSpeedKBps > 0)
            {
                try { format.SetWriteSpeed(Math.Max(1, options.WriteSpeedKBps / 2), false); } catch (COMException) { }
            }

            long startLba = format.NextWritableAddress;
            long imageBytes = new FileInfo(options.ImageFileName).Length;

            if (options.TestBurn)
            {
                progress?.Report(new BurnProgress(BurnPhase.Completed, 100, Message: "ISO image test burn validated."));
                return;
            }

            Imapi2Interop.SHCreateStreamOnFileEx(
                options.ImageFileName,
                Imapi2Interop.StgmRead | Imapi2Interop.StgmShareDenyWrite,
                0,
                false,
                null,
                out imageStream);

            var sink = new DiscFormat2DataEventSink(args =>
            {
                int bufPct = args.TotalSystemBuffer > 0
                    ? (int)Math.Clamp(args.UsedSystemBuffer * 100.0 / args.TotalSystemBuffer, 0, 100)
                    : 100;

                if (args.CurrentAction == Imapi2Interop.WriteActionFinalization)
                {
                    progress?.Report(new BurnProgress(BurnPhase.ClosingSession, 99, bufPct, TimeSpan.FromSeconds(args.RemainingTime), Message: "Closing session..."));
                    return;
                }

                int pct = args.SectorCount > 0
                    ? (int)Math.Clamp((args.LastWrittenLba - args.StartLba) * 100.0 / args.SectorCount, 0, 100)
                    : 0;
                progress?.Report(new BurnProgress(
                    BurnPhase.Burning,
                    pct,
                    bufPct,
                    TimeSpan.FromSeconds(Math.Max(0, args.RemainingTime)),
                    Message: $"Writing image... {pct}%"));
            });

            _activeFormat = format;
            using (Imapi2Interop.AdviseEventSink(format, typeof(DDiscFormat2DataEvents).GUID, sink))
            {
                format.Write(imageStream);
            }

            if (options.VerifyData && !_isCancelled && !string.IsNullOrWhiteSpace(options.Drive.Letter))
            {
                progress?.Report(new BurnProgress(BurnPhase.Verifying, 0, Message: "Verifying image..."));
                ScsiOpticalDevice.VerifyStreamAgainstDisc(
                    options.Drive.Letter,
                    imageStream,
                    startLba,
                    imageBytes,
                    progress,
                    ct);
            }
        }
        finally
        {
            _activeFormat = null;
            Imapi2Interop.SafeRelease(imageStream);
            Imapi2Interop.SafeRelease(format);
            Imapi2Interop.SafeRelease(recorder);
        }
    }

    public async Task BurnMultiTrackAsync(
        DriveModel drive,
        Dictionary<int, GrabTrackInfo> tracks,
        int writeSpeedKBps = 0,
        bool finalizeDisc = true,
        bool ejectAfterBurn = true,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        _isCancelled = false;
        using var reg = ct.Register(Cancel);

        var ordered = tracks.OrderBy(k => k.Key).ToList();
        bool allAudio = ordered.Count > 0 && ordered.All(t => t.Value.IsAudio);

        if (allAudio)
        {
            var comp = new AudioCompilation();
            foreach (var kv in ordered)
            {
                comp.Tracks.Add(AudioTrackItem.FromFile(kv.Value.FileName));
            }
            var ab = new AudioBurner();
            _currentAudioBurner = ab;
            await ab.BurnAsync(
                comp,
                new AudioBurnOptions
                {
                    Drive = drive,
                    WriteSpeedKBps = writeSpeedKBps,
                    GapMode = AudioGapMode.TaoPause,
                    FinalizeDisc = finalizeDisc,
                    EjectAfterBurn = ejectAfterBurn
                },
                progress,
                ct);
            return;
        }

        // Data or mixed-mode multi-track copy
        int totalTracks = ordered.Count;
        int idx = 0;
        foreach (var kv in ordered)
        {
            if (_isCancelled || ct.IsCancellationRequested) break;
            idx++;
            bool isLast = idx == totalTracks;

            if (kv.Value.IsAudio)
            {
                var comp = new AudioCompilation();
                comp.Tracks.Add(AudioTrackItem.FromFile(kv.Value.FileName));
                var ab = new AudioBurner();
                _currentAudioBurner = ab;
                await ab.BurnAsync(
                    comp,
                    new AudioBurnOptions
                    {
                        Drive = drive,
                        WriteSpeedKBps = writeSpeedKBps,
                        GapMode = AudioGapMode.TaoPause,
                        FinalizeDisc = isLast && finalizeDisc,
                        EjectAfterBurn = isLast && ejectAfterBurn
                    },
                    progress,
                    ct);
            }
            else
            {
                await BurnAsync(
                    new ImageBurnOptions
                    {
                        Drive = drive,
                        ImageFileName = kv.Value.FileName,
                        WriteSpeedKBps = writeSpeedKBps,
                        FinalizeDisc = isLast && finalizeDisc,
                        EjectAfterBurn = isLast && ejectAfterBurn
                    },
                    progress,
                    ct);
            }
        }
    }

    public void Cancel()
    {
        _isCancelled = true;
        var fmt = _activeFormat;
        var ab = _currentAudioBurner;
        _ = Task.Run(() =>
        {
            try { fmt?.CancelWrite(); } catch { }
            try { ab?.Cancel(); } catch { }
        });
    }
}
