using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CDBurner10.Core.Native;

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
    private IDiscFormat2Data? _activeFormat;
    private bool _isCancelled;

    public bool IsCancelled => _isCancelled;

    public static (bool IsValid, string Message, long TotalBytes) ValidateVideoTsFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return (false, "VIDEO_TS directory does not exist.", 0);

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
                BurnWithImapi2(options, progress, ct);

                if (options.EjectAfterBurn && !_isCancelled)
                {
                    try { options.Drive.Eject(); } catch { }
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
            progress?.Report(new BurnProgress(BurnPhase.Preparing, 0, Message: "Building DVD-Video UDF 1.02 + ISO9660 bridge image..."));

            IFileSystemImage? fsi = null;
            IFileSystemImageResult? result = null;
            IStream? imageStream = null;
            var openedStreams = new List<IStream>();

            try
            {
                fsi = BuildDvdVideoImage(options, recorder: null, openedStreams);
                result = fsi.CreateResultImage();
                imageStream = result.ImageStream;
                long totalBytes = (long)result.TotalBlocks * result.BlockSize;

                Imapi2Interop.CopyIStreamToFile(
                    imageStream,
                    outputIsoPath,
                    totalBytes,
                    pct => progress?.Report(new BurnProgress(BurnPhase.Burning, pct, Message: $"Saving DVD-Video ISO... {pct}%")),
                    ct);
            }
            finally
            {
                foreach (var s in openedStreams) Imapi2Interop.SafeRelease(s);
                Imapi2Interop.SafeRelease(imageStream);
                Imapi2Interop.SafeRelease(result);
                Imapi2Interop.SafeRelease(fsi);
            }

            progress?.Report(new BurnProgress(BurnPhase.Completed, 100, Message: "DVD-Video ISO saved successfully."));
        }, ct);
    }

    private void BurnWithImapi2(
        DvdVideoBurnOptions options,
        IProgress<BurnProgress>? progress,
        CancellationToken ct)
    {
        IDiscRecorder2? recorder = null;
        IDiscFormat2Data? format = null;
        IFileSystemImage? fsi = null;
        IFileSystemImageResult? result = null;
        IStream? imageStream = null;
        var openedStreams = new List<IStream>();

        try
        {
            recorder = options.Drive!.OpenRecorder();
            format = Imapi2Interop.CreateDiscFormat2Data();
            if (!format.IsRecorderSupported(recorder))
                throw new InvalidOperationException($"Drive '{options.Drive.DisplayName}' does not support DVD burning.");
            if (!format.IsCurrentMediaSupported(recorder))
                throw new InvalidOperationException("No writable DVD disc detected in the selected drive.");

            format.put_Recorder(recorder);
            format.put_ClientName("CDBurner10");
            format.put_ForceMediaToBeClosed(true);
            format.put_DisableConsumerDvdCompatibilityMode(!options.Padding);
            format.put_BufferUnderrunFreeDisabled(false);

            if (options.WriteSpeedKBps > 0)
            {
                try { format.SetWriteSpeed(Math.Max(1, options.WriteSpeedKBps / 2), false); } catch (COMException) { }
            }

            long startLba = format.NextWritableAddress;
            fsi = BuildDvdVideoImage(options, recorder, openedStreams);
            result = fsi.CreateResultImage();
            imageStream = result.ImageStream;
            long totalBytes = (long)result.TotalBlocks * result.BlockSize;

            if (options.TestBurn)
            {
                progress?.Report(new BurnProgress(BurnPhase.Completed, 100, Message: "DVD-Video test burn validated."));
                return;
            }

            var sink = new DiscFormat2DataEventSink(args =>
            {
                int bufPct = args.TotalSystemBuffer > 0
                    ? (int)Math.Clamp(args.UsedSystemBuffer * 100.0 / args.TotalSystemBuffer, 0, 100)
                    : 100;

                if (args.CurrentAction == Imapi2Interop.WriteActionFinalization)
                {
                    progress?.Report(new BurnProgress(BurnPhase.ClosingSession, 99, bufPct, TimeSpan.FromSeconds(args.RemainingTime), Message: "Closing DVD session..."));
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
                    Message: $"Writing DVD-Video... {pct}%"));
            });

            _activeFormat = format;
            using (Imapi2Interop.AdviseEventSink(format, typeof(DDiscFormat2DataEvents).GUID, sink))
            {
                format.Write(imageStream);
            }

            if (options.VerifyData && !_isCancelled && !string.IsNullOrWhiteSpace(options.Drive.Letter))
            {
                progress?.Report(new BurnProgress(BurnPhase.Verifying, 0, Message: "Verifying DVD-Video..."));
                ScsiOpticalDevice.VerifyStreamAgainstDisc(
                    options.Drive.Letter,
                    imageStream,
                    startLba,
                    totalBytes,
                    progress,
                    ct);
            }
        }
        finally
        {
            _activeFormat = null;
            foreach (var s in openedStreams) Imapi2Interop.SafeRelease(s);
            Imapi2Interop.SafeRelease(imageStream);
            Imapi2Interop.SafeRelease(result);
            Imapi2Interop.SafeRelease(fsi);
            Imapi2Interop.SafeRelease(format);
            Imapi2Interop.SafeRelease(recorder);
        }
    }

    private static IFileSystemImage BuildDvdVideoImage(
        DvdVideoBurnOptions options,
        IDiscRecorder2? recorder,
        List<IStream> openedStreams)
    {
        var fsi = Imapi2Interop.CreateFileSystemImage();
        // DVD-Video specification requires ISO9660 + UDF 1.02 bridge
        fsi.FileSystemsToCreate = Imapi2Interop.FsiFileSystemISO9660 | Imapi2Interop.FsiFileSystemUDF;
        fsi.put_UDFRevision(0x102);

        if (recorder is not null)
        {
            try { fsi.SetMaxMediaBlocksFromDevice(recorder); } catch (COMException) { }
        }
        else
        {
            fsi.FreeMediaBlocks = 0;
        }

        string label = DataCompilation.ReplaceDiscLabelVariables(options.VolumeName);
        if (string.IsNullOrWhiteSpace(label)) label = "DVD_VIDEO";
        if (label.Length > 32) label = label[..32];
        fsi.VolumeName = label;

        string videoTsSource = options.VideoTsDirectory;
        string nestedVideoTs = Path.Combine(videoTsSource, "VIDEO_TS");
        if (Directory.Exists(nestedVideoTs)) videoTsSource = nestedVideoTs;

        fsi.Root.AddDirectory("AUDIO_TS");
        fsi.Root.AddDirectory("VIDEO_TS");

        if (fsi.Root["VIDEO_TS"] is IFsiDirectoryItem videoDir)
        {
            // Sort VIDEO_TS files in DVD-Video spec order: VIDEO_TS.IFO, VIDEO_TS.VOB, VIDEO_TS.BUP, then VTS_xx_*
            var orderedFiles = Directory.GetFiles(videoTsSource)
                .OrderBy(f => GetDvdFileSortKey(Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var file in orderedFiles)
            {
                Imapi2Interop.SHCreateStreamOnFileEx(
                    file,
                    Imapi2Interop.StgmRead | Imapi2Interop.StgmShareDenyWrite,
                    0,
                    false,
                    null,
                    out var stream);
                openedStreams.Add(stream);
                videoDir.AddFile(Path.GetFileName(file).ToUpperInvariant(), stream);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.AudioTsDirectory) && Directory.Exists(options.AudioTsDirectory))
        {
            if (fsi.Root["AUDIO_TS"] is IFsiDirectoryItem audioDir)
            {
                foreach (var file in Directory.GetFiles(options.AudioTsDirectory))
                {
                    Imapi2Interop.SHCreateStreamOnFileEx(
                        file,
                        Imapi2Interop.StgmRead | Imapi2Interop.StgmShareDenyWrite,
                        0,
                        false,
                        null,
                        out var stream);
                    openedStreams.Add(stream);
                    audioDir.AddFile(Path.GetFileName(file).ToUpperInvariant(), stream);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ExtraFilesDirectory) && Directory.Exists(options.ExtraFilesDirectory))
        {
            fsi.Root.AddTree(options.ExtraFilesDirectory, includeBaseDirectory: false);
        }

        return fsi;
    }

    private static string GetDvdFileSortKey(string fileName)
    {
        string upper = fileName.ToUpperInvariant();
        if (upper == "VIDEO_TS.IFO") return "00_0";
        if (upper == "VIDEO_TS.VOB") return "00_1";
        if (upper == "VIDEO_TS.BUP") return "00_2";
        if (upper.StartsWith("VTS_") && upper.Length >= 10)
        {
            string title = upper[4..6];
            string extOrder = upper.EndsWith(".IFO") ? "0" : upper.EndsWith(".VOB") ? "1" : "2";
            return $"01_{title}_{extOrder}_{upper}";
        }
        return $"99_{upper}";
    }

    public void Cancel()
    {
        _isCancelled = true;
        var fmt = _activeFormat;
        if (fmt is not null)
        {
            _ = Task.Run(() =>
            {
                try { fmt.CancelWrite(); } catch { }
            });
        }
    }
}
