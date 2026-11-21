using System.Runtime.InteropServices;
using RocketDivision.StarBurnX;

namespace CDBurner10.Core.Burning;

public sealed class ImageBurnOptions
{
    public required DriveModel Drive { get; init; }
    public required string ImageFileName { get; init; }
    public int WriteSpeedKBps { get; init; } = 0;
    public WriteMode Mode { get; init; } = WriteMode.Auto;
    public bool IsMdsImage { get; init; } = false;
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
    private ImageBurnerClass? _burner;
    private AudioBurnerClass? _currentAudioBurner;
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

            bool isMds = options.IsMdsImage ||
                         Path.GetExtension(options.ImageFileName).Equals(".mds", StringComparison.OrdinalIgnoreCase);

            int copies = Math.Max(1, options.Copies);
            for (int copy = 1; copy <= copies; copy++)
            {
                ct.ThrowIfCancellationRequested();

                var burner = new ImageBurnerClass
                {
                    Drive = options.Drive.RawDrive,
                    ImageFileName = options.ImageFileName,
                    Mode = GenericDataBurner.MapWriteMode(options.Mode),
                    IsXACDROM = options.IsXaCdRom,
                    Padding = options.Padding,
                    NextSessionAllowed = !options.FinalizeDisc
                };
                if (options.FinalizeDisc) burner.KeepSessionOpened = false;

                if (options.WriteSpeedKBps > 0)
                {
                    try { burner.WriteSpeed = options.WriteSpeedKBps; } catch (COMException) { }
                }

                int lastBuf = 0;
                burner.OnBuffer += p => lastBuf = p;
                burner.OnProgress += (p, rem) =>
                    progress?.Report(new BurnProgress(BurnPhase.Burning, p, lastBuf, TimeSpan.FromSeconds(rem), Message: $"Writing image... {p}%"));
                burner.OnCloseSessionBegin += () =>
                    progress?.Report(new BurnProgress(BurnPhase.ClosingSession, 99, lastBuf, Message: "Closing session..."));
                burner.OnVerify += (p, rem) =>
                    progress?.Report(new BurnProgress(BurnPhase.Verifying, p, 0, TimeSpan.FromSeconds(rem), Message: $"Verifying image... {p}%"));

                _burner = burner;

                try
                {
                    if (isMds || options.IsXaCdRom)
                    {
                        try
                        {
                            burner.BurnEx(options.TestBurn, isMds, options.IsXaCdRom);
                        }
                        catch (COMException ex) when (isMds && ex.IsError(STARBURN_RESULT_CODES.STARBURN_ERR_WRITEMODE_NOT_SUPPORTED))
                        {
                            burner.Mode = STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_SAO;
                            burner.BurnEx(options.TestBurn, isMds, options.IsXaCdRom);
                        }
                    }
                    else
                    {
                        burner.Burn(options.TestBurn);
                    }

                    if (options.VerifyData && !_isCancelled)
                    {
                        burner.Verify();
                    }
                }
                catch (COMException ex) when (isMds && ex.IsError(STARBURN_RESULT_CODES.STARBURN_ERR_WRITEMODE_NOT_SUPPORTED))
                {
                    burner.Mode = STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_SAO;
                    burner.BurnEx(options.TestBurn, isMds, options.IsXaCdRom);
                }

                if (options.EjectAfterBurn && !_isCancelled)
                {
                    try { options.Drive.RawDrive.Eject(); } catch (COMException) { }
                }
            }

            progress?.Report(new BurnProgress(
                _isCancelled ? BurnPhase.Cancelled : BurnPhase.Completed,
                100,
                Message: _isCancelled ? "Image burn cancelled." : "Image burned successfully."));
        }, ct);
    }

    public Task BurnMultiTrackAsync(
        DriveModel drive,
        Dictionary<int, GrabTrackInfo> tracks,
        int writeSpeedKBps = 0,
        bool finalizeDisc = true,
        bool ejectAfterBurn = true,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            _isCancelled = false;
            using var reg = ct.Register(Cancel);

            int totalLb = Math.Max(1, tracks.Values.Sum(t => t.TrackSizeLB));
            int writtenLb = 0;

            foreach (var kv in tracks.OrderBy(k => k.Key))
            {
                if (_isCancelled || ct.IsCancellationRequested) break;

                int trackNum = kv.Key;
                var info = kv.Value;
                bool isLast = trackNum == tracks.Count;
                bool nextSessionAllowed = !isLast || !finalizeDisc;

                if (info.IsAudio)
                {
                    var ab = new AudioBurnerClass
                    {
                        Drive = drive.RawDrive,
                        Mode = STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_TAO,
                        KeepSessionOpened = tracks.TryGetValue(trackNum + 1, out var next) && next.IsAudio,
                        NextSessionAllowed = nextSessionAllowed
                    };
                    if (writeSpeedKBps > 0)
                    {
                        try { ab.WriteSpeed = writeSpeedKBps; } catch (COMException) { }
                    }
                    ab.AddFile(info.FileName);
                    ab.OnProgress += (pct, rem) =>
                    {
                        int cur = (int)(pct / 100.0 * info.TrackSizeLB);
                        int overall = (int)Math.Clamp((writtenLb + cur) * 100.0 / totalLb, 0, 100);
                        progress?.Report(new BurnProgress(BurnPhase.Burning, overall, TimeRemaining: TimeSpan.FromSeconds(rem), CurrentTrack: trackNum, TotalTracks: tracks.Count));
                    };
                    _currentAudioBurner = ab;
                    ab.Burn(false);
                }
                else
                {
                    var ib = new ImageBurnerClass
                    {
                        Drive = drive.RawDrive,
                        ImageFileName = info.FileName,
                        Mode = STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_TAO,
                        NextSessionAllowed = nextSessionAllowed
                    };
                    if (writeSpeedKBps > 0)
                    {
                        try { ib.WriteSpeed = writeSpeedKBps; } catch (COMException) { }
                    }
                    ib.OnProgress += (pct, rem) =>
                    {
                        int cur = (int)(pct / 100.0 * info.TrackSizeLB);
                        int overall = (int)Math.Clamp((writtenLb + cur) * 100.0 / totalLb, 0, 100);
                        progress?.Report(new BurnProgress(BurnPhase.Burning, overall, TimeRemaining: TimeSpan.FromSeconds(rem), CurrentTrack: trackNum, TotalTracks: tracks.Count));
                    };
                    _burner = ib;
                    ib.BurnEx(false, false, true);
                }

                writtenLb += info.TrackSizeLB;
            }

            if (ejectAfterBurn && !_isCancelled)
            {
                try { drive.RawDrive.Eject(); } catch (COMException) { }
            }

            progress?.Report(new BurnProgress(
                _isCancelled ? BurnPhase.Cancelled : BurnPhase.Completed,
                100,
                Message: _isCancelled ? "Multi-track burn cancelled." : "Multi-track burn completed."));
        }, ct);
    }

    public void Cancel()
    {
        _isCancelled = true;
        _ = Task.Run(() =>
        {
            try { _burner?.Cancel(); } catch { }
            try { _currentAudioBurner?.Cancel(); } catch { }
        });
    }
}
