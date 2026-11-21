using System.Runtime.InteropServices;
using CDBurner10.Core.Audio;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Dvd;
using CDBurner10.Core.Utilities;

namespace CDBurner10.Cli;

internal static class Commands
{
    public static void ListDrives(HashSet<string> flags)
    {
        bool writeOnly = flags.Contains("-writeonly");
        var drives = BurningEngine.Drives;
        foreach (var drive in drives)
        {
            if (!writeOnly || drive.IsWriter)
            {
                Console.WriteLine("{0:0}: {1} {2} ({3}:\\)", drive.Index, drive.Vendor, drive.Product, drive.Letter);
            }
        }
    }

    private static DriveModel? GetValidDrive(short deviceIndex)
    {
        var drive = BurningEngine.GetDrive(deviceIndex);
        if (drive is null)
        {
            Console.WriteLine("Drive index '{0}' is invalid.", deviceIndex);
            return null;
        }
        return drive;
    }

    public static void Eject(short deviceIndex)
    {
        var drive = GetValidDrive(deviceIndex);
        if (drive is null) return;
        Console.WriteLine("Ejecting disc...");
        drive.Eject();
    }

    public static void Load(short deviceIndex)
    {
        var drive = GetValidDrive(deviceIndex);
        if (drive is null) return;
        Console.WriteLine("Closing tray...");
        drive.Load();
    }

    public static void WaitForDisc(short deviceIndex)
    {
        var drive = GetValidDrive(deviceIndex);
        if (drive is null) return;
        while (drive.State != 0)
        {
            Thread.Sleep(500);
            drive.GetDiscInfo(refresh: true);
        }
    }

    public static void Erase(short deviceIndex)
    {
        var drive = GetValidDrive(deviceIndex);
        if (drive is null) return;

        var disc = drive.GetDiscInfo(refresh: true);
        if (!disc.HasDisc)
        {
            Console.WriteLine("No disc loaded in drive.");
            return;
        }
        if (!disc.IsErasable)
        {
            Console.WriteLine("Disc is not erasable.");
            return;
        }

        Console.WriteLine("Erasing disc, please wait...");
        var service = new DiscEraserService();
        var progress = new Progress<EraseProgress>(p => Console.WriteLine("{0}%", p.Percent));
        service.EraseAsync(drive, fullErase: false, ejectAfterErase: false, progress).GetAwaiter().GetResult();
        Console.WriteLine("Finished erasing the disc.");
    }

    public static void BurnIso(short deviceIndex, CommandLineOptions options)
    {
        var drive = GetValidDrive(deviceIndex);
        if (drive is null) return;

        if (!options.HasAtLeastOneFile())
        {
            Console.WriteLine("No ISO image file specified.");
            return;
        }

        string sourcePath = options.GetFirstFile().SourcePath;
        Console.WriteLine("Starting to burn ISO image from {0}...", sourcePath);

        var burner = new ImageBurner();
        var burnOptions = new ImageBurnOptions
        {
            Drive = drive,
            ImageFileName = sourcePath,
            WriteSpeedKBps = ResolveSpeedKBps(drive, options),
            FinalizeDisc = options.Flags.Contains("-close"),
            EjectAfterBurn = options.Flags.Contains("-eject")
        };

        var progress = new Progress<BurnProgress>(p =>
        {
            if (p.Phase == BurnPhase.Burning) Console.WriteLine("{0}%", p.Percent);
        });

        burner.BurnAsync(burnOptions, progress).GetAwaiter().GetResult();
    }

    public static void BurnData(short deviceIndex, CommandLineOptions options)
    {
        bool saveIsoOnly = options.Arguments.ContainsKey("-iso");
        DriveModel? drive = null;
        if (!saveIsoOnly)
        {
            drive = GetValidDrive(deviceIndex);
            if (drive is null) return;
        }

        var burner = new GenericDataBurner
        {
            FileDateTimeMode = options.Flags.Contains("-changefiledates")
                ? FileDateMode.Current
                : FileDateMode.Original,
            UseUtcFileDates = true
        };

        if (options.Arguments.TryGetValue("-udf", out var udfArg))
        {
            burner.SetFileSystemType(DataFileSystem.Udf);
            burner.UdfVersion = udfArg switch
            {
                "2.6" => UdfVersion.V2_60,
                "2.5" => UdfVersion.V2_50,
                "2.01" => UdfVersion.V2_01,
                "2.0" => UdfVersion.V2_00,
                "1.5" => UdfVersion.V1_50,
                _ => UdfVersion.V1_02
            };
        }

        if (options.Arguments.TryGetValue("-format", out var formatArg) &&
            formatArg.Equals("iso", StringComparison.OrdinalIgnoreCase))
        {
            burner.SetFileSystemType(DataFileSystem.IsoJoliet);
        }

        if (options.Arguments.TryGetValue("-layout", out var layoutPath))
        {
            var compilation = new DataCompilation(burner);
            Console.WriteLine("Loading compilation...");
            try
            {
                compilation.Load(layoutPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return;
            }
        }
        else
        {
            if (drive is not null && options.Flags.Contains("-import"))
            {
                try
                {
                    burner.ImportTrack(drive, -1);
                    Console.WriteLine("Existing layout imported successfully.");
                }
                catch (COMException ex)
                {
                    Console.WriteLine("Could not import existing layout ({0}).", ex.Message);
                }
            }

            foreach (var item in options.FilesAndFolders)
            {
                if (item.IsFile)
                {
                    try
                    {
                        using (File.OpenRead(item.SourcePath)) { }
                        burner.AddFile(item.SourcePath, string.IsNullOrWhiteSpace(item.TargetPath) ? "\\" : item.TargetPath);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Warning: Skipping {0} because file cannot be accessed ({1}).", item.SourcePath, ex.Message);
                    }
                }
                else if (item.IsDir)
                {
                    var errors = burner.AddDirectory(item.SourcePath, string.IsNullOrWhiteSpace(item.TargetPath) ? "\\" : item.TargetPath);
                    foreach (var err in errors)
                    {
                        Console.WriteLine("Warning: Skipping {0} ({1}).", err.FilePath, err.Message);
                    }
                }
            }
        }

        string volumeName = options.Arguments.TryGetValue("-name", out var nameArg) ? nameArg : string.Empty;

        var progress = new Progress<BurnProgress>(p =>
        {
            if (p.Phase == BurnPhase.Burning) Console.WriteLine("{0}%", p.Percent);
            else if (p.Phase == BurnPhase.Verifying) Console.WriteLine("Verifying: {0}%", p.Percent);
        });

        if (saveIsoOnly)
        {
            string isoPath = options.Arguments["-iso"].Trim();
            Console.WriteLine("Creating ISO image at {0}, {1} Files, Size: {2}",
                isoPath, burner.FileCount, FileSizeFormatter.Format(burner.TotalSizeBytes));
            burner.SaveIsoAsync(isoPath, volumeName, progress).GetAwaiter().GetResult();
            return;
        }

        var mode = WriteMode.Auto;
        if (options.Flags.Contains("-dao")) mode = WriteMode.DAO96;
        else if (options.Flags.Contains("-sao")) mode = WriteMode.SAO;
        else if (options.Flags.Contains("-tao")) mode = WriteMode.TAO;

        var burnOptions = new DataBurnOptions
        {
            Drive = drive!,
            WriteSpeedKBps = ResolveSpeedKBps(drive!, options),
            Mode = mode,
            FinalizeDisc = options.Flags.Contains("-close"),
            VerifyData = options.Flags.Contains("-verify"),
            EjectAfterBurn = options.Flags.Contains("-eject"),
            VolumeName = volumeName
        };

        Console.WriteLine("Starting to write disc...{0} Files, Size: {1}",
            burner.FileCount, FileSizeFormatter.Format(burner.TotalSizeBytes));
        burner.BurnAsync(burnOptions, progress).GetAwaiter().GetResult();
        Console.WriteLine("Burning data disc completed.");
    }

    public static void BurnAudio(short deviceIndex, CommandLineOptions options)
    {
        var drive = GetValidDrive(deviceIndex);
        if (drive is null) return;

        BassAudioEngine.Initialize();
        var compilation = new AudioCompilation();

        foreach (var item in options.FilesAndFolders)
        {
            if (item.IsFile)
            {
                AddAudioFile(compilation, item.SourcePath);
            }
            else if (item.IsDir)
            {
                foreach (var file in Directory.GetFiles(item.SourcePath))
                {
                    AddAudioFile(compilation, file);
                }
            }
        }

        if (compilation.Tracks.Count == 0)
        {
            Console.WriteLine("No tracks to burn.");
            return;
        }

        var burnOptions = new AudioBurnOptions
        {
            Drive = drive,
            WriteSpeedKBps = ResolveSpeedKBps(drive, options),
            GapMode = options.Flags.Contains("-dao") ? AudioGapMode.DaoGapless : AudioGapMode.TaoPause,
            FinalizeDisc = options.Flags.Contains("-close"),
            EjectAfterBurn = options.Flags.Contains("-eject")
        };

        Console.WriteLine("Starting to write disc...{0} tracks", compilation.Tracks.Count);
        var burner = new CDBurner10.Core.Burning.AudioBurner();
        var progress = new Progress<BurnProgress>(p =>
        {
            if (p.Phase == BurnPhase.Burning) Console.WriteLine("{0}%", p.Percent);
        });
        burner.BurnAsync(compilation, burnOptions, progress).GetAwaiter().GetResult();
        Console.WriteLine("Audio disc burned successfully.");
    }

    private static void AddAudioFile(AudioCompilation compilation, string file)
    {
        try
        {
            if (BassAudioEngine.IsAudioFile(file))
            {
                compilation.Tracks.Add(AudioTrackItem.FromFile(file));
                Console.WriteLine("Added: " + file);
            }
            else
            {
                Console.WriteLine("Skipped: " + file);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }

    public static void BurnVideo(short deviceIndex, CommandLineOptions options)
    {
        bool saveIsoOnly = options.Arguments.ContainsKey("-iso");
        DriveModel? drive = null;
        if (!saveIsoOnly)
        {
            drive = GetValidDrive(deviceIndex);
            if (drive is null) return;
        }

        string videoDir = options.FilesAndFolders.FirstOrDefault(f => f.IsDir).SourcePath ?? string.Empty;
        string volumeName = options.Arguments.TryGetValue("-name", out var nameArg) ? nameArg : string.Empty;

        var burnOptions = new DvdVideoBurnOptions
        {
            Drive = drive,
            VideoTsDirectory = videoDir,
            VolumeName = volumeName,
            Padding = true,
            WriteSpeedKBps = drive is not null ? ResolveSpeedKBps(drive, options) : 0,
            EjectAfterBurn = options.Flags.Contains("-eject")
        };

        var burner = new DvdVideoBurner();
        var progress = new Progress<BurnProgress>(p =>
        {
            if (p.Phase == BurnPhase.Burning) Console.WriteLine("{0}%", p.Percent);
        });

        if (saveIsoOnly)
        {
            string isoPath = options.Arguments["-iso"].Trim();
            Console.WriteLine("Creating ISO image at {0}, using folder \"{1}\"", isoPath, videoDir);
            burner.SaveIsoAsync(burnOptions, isoPath, progress).GetAwaiter().GetResult();
            return;
        }

        Console.WriteLine("Starting to write disc using folder \"{0}\"", videoDir);
        burner.BurnAsync(burnOptions, progress).GetAwaiter().GetResult();
        Console.WriteLine("Burning video disc completed.");
    }

    public static int DvdToMp4(CommandLineOptions options)
    {
        if (!DvdVideoConverter.IsAvailable)
        {
            Console.WriteLine(DvdVideoConverter.InstallHint);
            return -1;
        }

        string? source = options.FilesAndFolders.FirstOrDefault().SourcePath;
        if (string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(options.RawDeviceArg))
        {
            if (DvdVideoConverter.TryExtractDriveLetter(options.RawDeviceArg, out char driveLetter))
            {
                source = DvdVideoConverter.GetDriveSourcePath(driveLetter.ToString());
            }
            else if (short.TryParse(options.RawDeviceArg, out short devIdx))
            {
                BurningEngine.Initialize();
                try
                {
                    var drive = BurningEngine.GetDrive(devIdx);
                    if (drive is not null && !string.IsNullOrWhiteSpace(drive.Letter))
                        source = DvdVideoConverter.GetDriveSourcePath(drive.Letter);
                }
                finally
                {
                    BurningEngine.Uninitialize();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine("Specify a source optical drive (-device:<letter|index>), DVD ISO (-file:<path>), or VIDEO_TS folder (-folder:<path>).");
            return -1;
        }

        string destination;
        if (options.Arguments.TryGetValue("-out", out var outArg) && !string.IsNullOrWhiteSpace(outArg))
        {
            destination = outArg.Trim();
        }
        else if (DvdVideoConverter.TryExtractDriveLetter(source, out char dl))
        {
            string label = DvdVideoConverter.GetSuggestedDriveOutputName(dl.ToString());
            destination = Path.Combine(Environment.CurrentDirectory, label + ".mp4");
        }
        else
        {
            destination = Path.ChangeExtension(Path.GetFullPath(source), ".mp4");
        }

        int titleNumber = options.Arguments.TryGetValue("-title", out var titleArg) && int.TryParse(titleArg, out var t)
            ? Math.Clamp(t, 0, 99)
            : 0;
        int audioOrdinal = options.Arguments.TryGetValue("-audio", out var audioArg) && int.TryParse(audioArg, out var a)
            ? Math.Max(0, a)
            : 0;
        int? subtitleOrdinal = options.Arguments.TryGetValue("-sub", out var subArg) && int.TryParse(subArg, out var s)
            ? Math.Max(0, s)
            : null;
        int crf = options.Arguments.TryGetValue("-crf", out var crfArg) && int.TryParse(crfArg, out var c)
            ? Math.Clamp(c, 0, 51)
            : 21;
        string preset = options.Arguments.TryGetValue("-preset", out var presetArg) && !string.IsNullOrWhiteSpace(presetArg)
            ? presetArg.Trim()
            : "medium";
        bool stereoDownmix = !options.Flags.Contains("-no-downmix");
        bool deinterlace = !options.Flags.Contains("-no-deinterlace");
        var encoder = options.Flags.Contains("-sw") ? DvdVideoEncoder.Software : DvdVideoEncoder.Auto;

        try
        {
            string resolvedEncoder = DvdVideoConverter.ResolveVideoEncoder(encoder);
            Console.WriteLine("Probing DVD-Video source \"{0}\" (encoder: {1})...", source, resolvedEncoder);
            var info = DvdVideoConverter.ProbeAsync(source, titleNumber).GetAwaiter().GetResult();
            Console.WriteLine("Detected title: {0:hh\\:mm\\:ss}, {1}x{2}, {3} audio, {4} subtitle track(s).",
                info.Duration, info.Width, info.Height, info.AudioTracks.Count, info.SubtitleTracks.Count);

            var mp4Options = new DvdMp4Options
            {
                TitleNumber = titleNumber,
                AudioOrdinal = audioOrdinal,
                SubtitleOrdinal = subtitleOrdinal,
                Crf = crf,
                Preset = preset,
                StereoDownmix = stereoDownmix,
                Deinterlace = deinterlace,
                Encoder = encoder
            };

            int lastPercent = -1;
            var progress = new Progress<DvdMp4Progress>(p =>
            {
                if (p.Percent != lastPercent)
                {
                    lastPercent = p.Percent;
                    Console.WriteLine("{0}% ({1:0.0}x, {2:0} fps)", p.Percent, p.Speed, p.Fps);
                }
            });

            Console.WriteLine("Converting to \"{0}\"...", destination);
            DvdVideoConverter.ConvertAsync(source, destination, info, mp4Options, progress).GetAwaiter().GetResult();
            Console.WriteLine("DVD to MP4 conversion completed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("DVD to MP4 conversion failed: " + ex.Message);
            return -1;
        }
    }

    private static int ResolveSpeedKBps(DriveModel drive, CommandLineOptions options)
    {
        if (!options.Arguments.TryGetValue("-speed", out var speedArg))
            return 0;

        foreach (var sp in drive.SupportedWriteSpeeds)
        {
            if (sp.SpeedKBps > 0 &&
                sp.SpeedRatio.ToString().TrimEnd('0').TrimEnd('.') == speedArg.Trim())
            {
                return sp.SpeedKBps;
            }
        }

        return 0;
    }
}
