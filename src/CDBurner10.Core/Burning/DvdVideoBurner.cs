using System.Runtime.InteropServices;
using RocketDivision.StarBurnX;

namespace CDBurner10.Core.Burning;

public sealed class DvdVideoBurnOptions
{
    public DriveModel? Drive { get; init; }
    public required string VideoTsDirectory { get; init; }
    public string? AudioTsDirectory { get; init; }
    public string? ExtraFilesDirectory { get; init; }
    public string VolumeName { get; init; } = "DVD_VIDEO";
    public string PublisherName { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = "CDBurner10";
    public int WriteSpeedKBps { get; init; } = 0;
    public bool DoubleLayerCompatibleMode { get; init; } = true;
    public bool Padding { get; init; } = true;
    public bool PatchingIfoBup { get; init; } = true;
    public bool VerifyData { get; init; } = false;
    public bool TestBurn { get; init; } = false;
    public bool EjectAfterBurn { get; init; } = true;
    public int Copies { get; init; } = 1;
}

public sealed class DvdVideoBurner
{
    private DVDVideoBurnerClass? _burner;
    private bool _isCancelled;

    public bool IsCancelled => _isCancelled;

    public static (bool IsValid, string Message, long TotalBytes) ValidateVideoTsFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return (false, "VIDEO_TS directory does not exist.", 0);

        // If user selected parent folder containing VIDEO_TS, allow checking inside
        string effectiveDir = folderPath;
        var nested = Path.Combine(folderPath, "VIDEO_TS");
        if (Directory.Exists(nested)) effectiveDir = nested;

        var files = Directory.GetFiles(effectiveDir);
        bool hasIfo = files.Any(f => f.EndsWith(".ifo", StringComparison.OrdinalIgnoreCase));
        bool hasBup = files.Any(f => f.EndsWith(".bup", StringComparison.OrdinalIgnoreCase));
        bool hasVob = files.Any(f => f.EndsWith(".vob", StringComparison.OrdinalIgnoreCase));

        long totalSize = files.Sum(f => new FileInfo(f).Length);

        if (!hasIfo || !hasVob)
            return (false, "Folder must contain valid DVD-Video files (.IFO, .BUP, and .VOB).", totalSize);

        return (true, hasBup ? "Valid VIDEO_TS folder." : "Valid VIDEO_TS folder (warning: no .BUP backup files found).", totalSize);
    }

    public Task BurnAsync(
        DvdVideoBurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (options.Drive is null)
                throw new InvalidOperationException("Target optical drive is required.");

            var validation = ValidateVideoTsFolder(options.VideoTsDirectory);
            if (!validation.IsValid)
                throw new InvalidOperationException(validation.Message);

            _isCancelled = false;
            using var reg = ct.Register(Cancel);

            int copies = Math.Max(1, options.Copies);
            for (int copy = 1; copy <= copies; copy++)
            {
                ct.ThrowIfCancellationRequested();
                var burner = CreateConfiguredBurner(options);
                burner.Drive = options.Drive.RawDrive;
                if (options.WriteSpeedKBps > 0)
                {
                    try { burner.WriteSpeed = options.WriteSpeedKBps; } catch (COMException) { }
                }

                WireEvents(burner, progress);
                _burner = burner;

                string label = DataCompilation.ReplaceDiscLabelVariables(options.VolumeName);
                burner.Burn(options.TestBurn, label, options.PublisherName, options.ApplicationName);

                if (options.VerifyData && !_isCancelled)
                {
                    burner.Verify();
                }

                if (options.EjectAfterBurn && !_isCancelled)
                {
                    try { options.Drive.RawDrive.Eject(); } catch (COMException) { }
                }
            }

            progress?.Report(new BurnProgress(
                _isCancelled ? BurnPhase.Cancelled : BurnPhase.Completed,
                100,
                Message: _isCancelled ? "DVD-Video burn cancelled." : "DVD-Video burned successfully."));
        }, ct);
    }

    public Task SaveIsoAsync(
        DvdVideoBurnOptions options,
        string outputIsoPath,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var validation = ValidateVideoTsFolder(options.VideoTsDirectory);
            if (!validation.IsValid)
                throw new InvalidOperationException(validation.Message);

            var dir = Path.GetDirectoryName(outputIsoPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _isCancelled = false;
            using var reg = ct.Register(Cancel);

            var burner = CreateConfiguredBurner(options);
            WireEvents(burner, progress);
            _burner = burner;

            string label = DataCompilation.ReplaceDiscLabelVariables(options.VolumeName);
            burner.SaveImageFile(outputIsoPath, label, options.PublisherName, options.ApplicationName);

            progress?.Report(new BurnProgress(BurnPhase.Completed, 100, Message: "DVD-Video ISO saved successfully."));
        }, ct);
    }

    public void Cancel()
    {
        _isCancelled = true;
        _ = Task.Run(() =>
        {
            try { _burner?.Cancel(); } catch { }
        });
    }

    private static DVDVideoBurnerClass CreateConfiguredBurner(DvdVideoBurnOptions options)
    {
        var burner = new DVDVideoBurnerClass
        {
            VideoDirectory = options.VideoTsDirectory,
            KeepSessionOpened = false,
            NextSessionAllowed = false,
            DoubleLayerCompatibleMode = options.DoubleLayerCompatibleMode,
            Padding = options.Padding,
            PatchingIFOBUP = options.PatchingIfoBup
        };
        if (!string.IsNullOrWhiteSpace(options.AudioTsDirectory))
            burner.AudioDirectory = options.AudioTsDirectory;
        if (!string.IsNullOrWhiteSpace(options.ExtraFilesDirectory))
            burner.ExtraFilesDirectory = options.ExtraFilesDirectory;
        return burner;
    }

    private static void WireEvents(DVDVideoBurnerClass burner, IProgress<BurnProgress>? progress)
    {
        int lastBuf = 0;
        burner.OnBuffer += p => lastBuf = p;
        burner.OnProgress += (p, rem) =>
            progress?.Report(new BurnProgress(BurnPhase.Burning, p, lastBuf, TimeSpan.FromSeconds(rem), Message: $"Writing DVD-Video... {p}%"));
        burner.OnCloseSessionBegin += () =>
            progress?.Report(new BurnProgress(BurnPhase.ClosingSession, 99, lastBuf, Message: "Closing DVD session..."));
        burner.OnVerify += (p, rem) =>
            progress?.Report(new BurnProgress(BurnPhase.Verifying, p, 0, TimeSpan.FromSeconds(rem), Message: $"Verifying DVD-Video... {p}%"));
    }
}
