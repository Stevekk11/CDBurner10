using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CDBurner10.Core.Native;

namespace CDBurner10.Core.Burning;

public sealed class GenericDataBurner
{
    private const long MaxIsoFileSizeBytes = 4L * 1024 * 1024 * 1024 - 1; // 4 GiB - 1

    private DataFileSystem _fileSystem = DataFileSystem.IsoJoliet;
    private IDiscFormat2Data? _activeFormat;
    private bool _isCancelled;
    private bool _isImported;

    public VirtualFolder RootFolder { get; } = new("Disc");

    public DataFileSystem FileSystemType => _fileSystem;
    public int NameMaxLength => _fileSystem == DataFileSystem.Udf ? 16 : 32;
    public int FileNameMaxLength => _fileSystem == DataFileSystem.Udf ? 127 : 107;
    public bool IsCancelled => _isCancelled;
    public bool IsImported => _isImported;

    public string VolumeName
    {
        get => RootFolder.Name;
        set => RootFolder.Name = value ?? string.Empty;
    }

    public string PublisherName { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = "CDBurner10";
    public UdfVersion UdfVersion { get; set; } = UdfVersion.V1_02;
    public FileDateMode FileDateTimeMode { get; set; } = FileDateMode.Original;
    public DateTime CustomFileDateTime { get; set; } = DateTime.Now;
    public bool UseUtcFileDates { get; set; } = true;
    public bool IsXaCdRom { get; set; }
    public bool AutoSortFiles { get; set; } = true;
    public bool DisableCollisionsDetection { get; set; }
    public bool CheckOverburning { get; set; }
    public BootImageOptions BootImage { get; set; } = new();

    public long TotalSizeBytes => RootFolder.TotalSizeBytes;
    public int FileCount => RootFolder.FileCount;
    public int FolderCount => RootFolder.FolderCount;

    public void SetFileSystemType(DataFileSystem fileSystem)
    {
        if (_fileSystem == fileSystem) return;

        if (_fileSystem == DataFileSystem.Udf && fileSystem == DataFileSystem.IsoJoliet)
        {
            var tooBig = RootFolder.EnumerateAllFiles().FirstOrDefault(f => f.FileSizeBytes > MaxIsoFileSizeBytes);
            if (tooBig is not null)
            {
                throw new FileTooBigException(tooBig.SourcePath, tooBig.FileSizeBytes);
            }
        }

        _fileSystem = fileSystem;
    }

    public VirtualFolder NewFolder(string virtualPath, bool hidden = false)
    {
        var folder = RootFolder.GetOrCreateSubFolder(virtualPath);
        folder.IsHidden = hidden;
        return folder;
    }

    public VirtualFile AddFile(string sourceFilePath, string targetVirtualFolder = "\\", string? nameOnDisc = null)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException($"File not found: '{sourceFilePath}'", sourceFilePath);

        var fi = new FileInfo(sourceFilePath);
        if (_fileSystem == DataFileSystem.IsoJoliet && fi.Length > MaxIsoFileSizeBytes)
        {
            throw new FileTooBigException(sourceFilePath, fi.Length);
        }

        var folder = RootFolder.GetOrCreateSubFolder(targetVirtualFolder);
        string name = string.IsNullOrWhiteSpace(nameOnDisc) ? fi.Name : nameOnDisc;

        var existing = folder.FindFile(name);
        if (existing is not null)
        {
            folder.Files.Remove(existing);
        }

        var vf = new VirtualFile(sourceFilePath, name, folder);
        folder.Files.Add(vf);
        return vf;
    }

    public VirtualFile AddFileToLocation(string sourceFilePath, string targetVirtualFilePath)
    {
        var dir = Path.GetDirectoryName(targetVirtualFilePath) ?? "\\";
        var name = Path.GetFileName(targetVirtualFilePath);
        return AddFile(sourceFilePath, dir, name);
    }

    public List<ItemAddError> AddDirectory(
        string sourceDirectory,
        string targetVirtualFolder = "\\",
        string? nameOnDisc = null,
        Func<string, bool>? fileFilter = null)
    {
        var errors = new List<ItemAddError>();
        if (!Directory.Exists(sourceDirectory))
        {
            errors.Add(new ItemAddError(sourceDirectory, "Directory does not exist."));
            return errors;
        }

        var dirName = string.IsNullOrWhiteSpace(nameOnDisc)
            ? new DirectoryInfo(sourceDirectory).Name
            : nameOnDisc;

        var parent = RootFolder.GetOrCreateSubFolder(targetVirtualFolder);
        var folder = parent.FindFolder(dirName);
        if (folder is null)
        {
            folder = new VirtualFolder(dirName, parent, sourceDirectory);
            parent.Folders.Add(folder);
        }

        AddDirectoryContentsRecursive(sourceDirectory, folder, fileFilter, errors);
        return errors;
    }

    private void AddDirectoryContentsRecursive(
        string sourceDir,
        VirtualFolder targetFolder,
        Func<string, bool>? fileFilter,
        List<ItemAddError> errors)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                if (fileFilter is not null && !fileFilter(file)) continue;
                try
                {
                    var fi = new FileInfo(file);
                    if (_fileSystem == DataFileSystem.IsoJoliet && fi.Length > MaxIsoFileSizeBytes)
                    {
                        errors.Add(new ItemAddError(file, "Exceeds 4 GiB ISO9660/Joliet limit.", new FileTooBigException(file, fi.Length)));
                        continue;
                    }
                    var existing = targetFolder.FindFile(fi.Name);
                    if (existing is not null) targetFolder.Files.Remove(existing);
                    targetFolder.Files.Add(new VirtualFile(file, fi.Name, targetFolder));
                }
                catch (Exception ex)
                {
                    errors.Add(new ItemAddError(file, ex.Message, ex, FileNameMaxLength));
                }
            }

            foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
            {
                try
                {
                    var di = new DirectoryInfo(subDir);
                    var subFolder = targetFolder.FindFolder(di.Name);
                    if (subFolder is null)
                    {
                        subFolder = new VirtualFolder(di.Name, targetFolder, subDir);
                        targetFolder.Folders.Add(subFolder);
                    }
                    AddDirectoryContentsRecursive(subDir, subFolder, fileFilter, errors);
                }
                catch (Exception ex)
                {
                    errors.Add(new ItemAddError(subDir, ex.Message, ex, FileNameMaxLength));
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(new ItemAddError(sourceDir, ex.Message, ex, FileNameMaxLength));
        }
    }

    public bool RemoveItem(VirtualItem item)
    {
        if (item.Parent is null) return false;
        if (item is VirtualFile vf) return item.Parent.Files.Remove(vf);
        if (item is VirtualFolder vd) return item.Parent.Folders.Remove(vd);
        return false;
    }

    public void MoveItem(VirtualItem item, VirtualFolder targetFolder)
    {
        if (item.Parent is null || ReferenceEquals(item.Parent, targetFolder)) return;
        RemoveItem(item);
        item.Parent = targetFolder;
        if (item is VirtualFile vf) targetFolder.Files.Add(vf);
        else if (item is VirtualFolder vd) targetFolder.Folders.Add(vd);
    }

    public void RenameItem(VirtualItem item, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        item.Name = newName.Trim();
    }

    public void Clear()
    {
        RootFolder.Clear();
        _isImported = false;
    }

    public List<string> FindFileNameLimitations()
    {
        var issues = new List<string>();
        int maxLen = FileNameMaxLength;
        foreach (var file in RootFolder.EnumerateAllFiles())
        {
            if (file.Name.Length > maxLen)
            {
                issues.Add($"{file.FullVirtualPath} ({file.Name.Length} chars > {maxLen})");
            }
        }
        return issues;
    }

    public void ImportTrack(DriveModel drive, int trackNumber = -1)
    {
        IDiscRecorder2? recorder = null;
        IDiscFormat2Data? format = null;
        IFileSystemImage? fsi = null;
        try
        {
            recorder = drive.OpenRecorder();
            format = Imapi2Interop.CreateDiscFormat2Data();
            format.put_Recorder(recorder);

            if (format.MediaPhysicallyBlank || format.MediaHeuristicallyBlank)
                throw new InvalidOperationException("The inserted disc is blank; there is no previous session to import.");

            fsi = Imapi2Interop.CreateFileSystemImage();
            fsi.MultisessionInterfaces = format.MultisessionInterfaces;
            int importedFs = fsi.ImportFileSystem();
            if ((importedFs & Imapi2Interop.FsiFileSystemUDF) != 0 && (importedFs & Imapi2Interop.FsiFileSystemJoliet) == 0)
            {
                _fileSystem = DataFileSystem.Udf;
            }
            else
            {
                _fileSystem = DataFileSystem.IsoJoliet;
            }

            PopulateVirtualTreeFromFsi(fsi.Root, RootFolder);
            _isImported = true;
        }
        finally
        {
            Imapi2Interop.SafeRelease(fsi);
            Imapi2Interop.SafeRelease(format);
            Imapi2Interop.SafeRelease(recorder);
        }
    }

    private static void PopulateVirtualTreeFromFsi(IFsiDirectoryItem sourceDir, VirtualFolder target)
    {
        var enumerator = sourceDir.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (enumerator.Current is IFsiDirectoryItem subDir)
            {
                var vf = target.FindFolder(subDir.Name)
                    ?? new VirtualFolder(subDir.Name, target) { IsImported = true, IsHidden = subDir.IsHidden };
                if (!target.Folders.Contains(vf)) target.Folders.Add(vf);
                PopulateVirtualTreeFromFsi(subDir, vf);
            }
            else if (enumerator.Current is IFsiFileItem fileItem)
            {
                if (target.FindFile(fileItem.Name) is null)
                {
                    target.Files.Add(new VirtualFile
                    {
                        Name = fileItem.Name,
                        SourcePath = string.Empty,
                        FileSizeBytes = fileItem.DataSize,
                        IsHidden = fileItem.IsHidden,
                        IsImported = true,
                        Parent = target
                    });
                }
            }
        }
    }

    public Task BurnAsync(
        DataBurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            _isCancelled = false;
            using var reg = ct.Register(Cancel);

            int copies = Math.Max(1, options.Copies);
            for (int copy = 1; copy <= copies; copy++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new BurnProgress(BurnPhase.Preparing, 0, Message: $"Preparing file system (copy {copy}/{copies})..."));

                string rawLabel = !string.IsNullOrWhiteSpace(options.VolumeName) ? options.VolumeName : VolumeName;
                string label = DataCompilation.ReplaceDiscLabelVariables(rawLabel);
                if (label.Length > NameMaxLength) label = label[..NameMaxLength];

                BurnWithImapi2(options, label, progress, ct);

                if (options.EjectAfterBurn && !_isCancelled)
                {
                    try { options.Drive.Eject(); } catch { }
                }
            }

            progress?.Report(new BurnProgress(
                _isCancelled ? BurnPhase.Cancelled : BurnPhase.Completed,
                100,
                Message: _isCancelled ? "Burn cancelled." : "Burn completed successfully."));
        }, ct);
    }

    private void BurnWithImapi2(
        DataBurnOptions options,
        string label,
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
            recorder = options.Drive.OpenRecorder();
            format = Imapi2Interop.CreateDiscFormat2Data();
            if (!format.IsRecorderSupported(recorder))
                throw new InvalidOperationException($"Drive '{options.Drive.DisplayName}' does not support data burning.");
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
            fsi = BuildFileSystemImage(label, recorder, format, openedStreams);
            result = fsi.CreateResultImage();
            imageStream = result.ImageStream;
            long totalImageBytes = (long)result.TotalBlocks * result.BlockSize;

            if (options.TestBurn)
            {
                // IMAPI2 does not expose hardware simulation burn; validate full image build cleanly
                progress?.Report(new BurnProgress(BurnPhase.Completed, 100, Message: "Test burn simulation verified (file system image built cleanly)."));
                return;
            }

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
                    Message: _fileSystem == DataFileSystem.Udf ? $"Writing UDF data... {pct}%" : $"Writing data... {pct}%"));
            });

            _activeFormat = format;
            using (Imapi2Interop.AdviseEventSink(format, typeof(DDiscFormat2DataEvents).GUID, sink))
            {
                format.Write(imageStream);
            }

            if (options.VerifyData && !_isCancelled && !string.IsNullOrWhiteSpace(options.Drive.Letter))
            {
                progress?.Report(new BurnProgress(BurnPhase.Verifying, 0, Message: "Verifying written disc sectors..."));
                ScsiOpticalDevice.VerifyStreamAgainstDisc(
                    options.Drive.Letter,
                    imageStream,
                    startLba,
                    totalImageBytes,
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

    public Task SaveIsoAsync(
        string outputIsoPath,
        string? volumeName = null,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            _isCancelled = false;

            var dir = Path.GetDirectoryName(outputIsoPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string rawLabel = !string.IsNullOrWhiteSpace(volumeName) ? volumeName : VolumeName;
            string label = DataCompilation.ReplaceDiscLabelVariables(rawLabel);
            if (label.Length > NameMaxLength) label = label[..NameMaxLength];

            progress?.Report(new BurnProgress(BurnPhase.Preparing, 0, Message: "Building ISO image..."));

            IFileSystemImage? fsi = null;
            IFileSystemImageResult? result = null;
            IStream? imageStream = null;
            var openedStreams = new List<IStream>();

            try
            {
                fsi = BuildFileSystemImage(label, recorder: null, format: null, openedStreams);
                result = fsi.CreateResultImage();
                imageStream = result.ImageStream;
                long totalBytes = (long)result.TotalBlocks * result.BlockSize;

                Imapi2Interop.CopyIStreamToFile(
                    imageStream,
                    outputIsoPath,
                    totalBytes,
                    pct => progress?.Report(new BurnProgress(
                        BurnPhase.Burning,
                        pct,
                        Message: _fileSystem == DataFileSystem.Udf ? $"Saving UDF ISO... {pct}%" : $"Saving ISO... {pct}%")),
                    ct);
            }
            finally
            {
                foreach (var s in openedStreams) Imapi2Interop.SafeRelease(s);
                Imapi2Interop.SafeRelease(imageStream);
                Imapi2Interop.SafeRelease(result);
                Imapi2Interop.SafeRelease(fsi);
            }

            progress?.Report(new BurnProgress(BurnPhase.Completed, 100, Message: "ISO saved successfully."));
        }, ct);
    }

    private IFileSystemImage BuildFileSystemImage(
        string label,
        IDiscRecorder2? recorder,
        IDiscFormat2Data? format,
        List<IStream> openedStreams)
    {
        var fsi = Imapi2Interop.CreateFileSystemImage();
        fsi.FileSystemsToCreate = _fileSystem == DataFileSystem.Udf
            ? Imapi2Interop.FsiFileSystemUDF
            : (Imapi2Interop.FsiFileSystemISO9660 | Imapi2Interop.FsiFileSystemJoliet);

        if (_fileSystem == DataFileSystem.Udf)
        {
            fsi.put_UDFRevision(MapUdfRevisionHex(UdfVersion));
        }

        if (recorder is not null && format is not null)
        {
            if (!format.MediaPhysicallyBlank && !format.MediaHeuristicallyBlank)
            {
                try
                {
                    fsi.MultisessionInterfaces = format.MultisessionInterfaces;
                    fsi.ImportFileSystem();
                }
                catch (COMException) { }
            }
            else
            {
                try { fsi.ChooseImageDefaults(recorder); } catch (COMException) { }
                // Re-apply user's chosen filesystem after defaults
                fsi.FileSystemsToCreate = _fileSystem == DataFileSystem.Udf
                    ? Imapi2Interop.FsiFileSystemUDF
                    : (Imapi2Interop.FsiFileSystemISO9660 | Imapi2Interop.FsiFileSystemJoliet);
                if (_fileSystem == DataFileSystem.Udf)
                    fsi.put_UDFRevision(MapUdfRevisionHex(UdfVersion));
            }
            fsi.FreeMediaBlocks = format.FreeSectorsOnMedia;
        }
        else
        {
            // Unlimited image blocks when authoring standalone ISO
            fsi.FreeMediaBlocks = 0;
        }

        fsi.VolumeName = string.IsNullOrWhiteSpace(label) ? "Disc" : label;
        ApplyBootImage(fsi, openedStreams);
        PopulateFsiTree(RootFolder, fsi.Root, openedStreams);
        return fsi;
    }

    private void ApplyBootImage(IFileSystemImage fsi, List<IStream> openedStreams)
    {
        if (!BootImage.Enabled || string.IsNullOrWhiteSpace(BootImage.FileName) || !File.Exists(BootImage.FileName))
            return;

        Imapi2Interop.SHCreateStreamOnFileEx(
            BootImage.FileName,
            Imapi2Interop.StgmRead | Imapi2Interop.StgmShareDenyWrite,
            0,
            false,
            null,
            out var bootStream);
        openedStreams.Add(bootStream);

        var boot = Imapi2Interop.CreateBootOptions();
        boot.Manufacturer = "CDBurner10";
        boot.PlatformId = BootImage.Platform;
        boot.Emulation = (int)BootImage.EmulationType;
        boot.AssignBootImage(bootStream);
        fsi.BootImageOptions = boot;
        Imapi2Interop.SafeRelease(boot);
    }

    private void PopulateFsiTree(VirtualFolder source, IFsiDirectoryItem target, List<IStream> openedStreams)
    {
        var files = AutoSortFiles
            ? source.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : source.Files.ToList();

        foreach (var file in files)
        {
            if (file.IsImported || string.IsNullOrWhiteSpace(file.SourcePath) || !File.Exists(file.SourcePath))
                continue;

            string safeName = file.Name.Length > FileNameMaxLength ? file.Name[..FileNameMaxLength] : file.Name;
            Imapi2Interop.SHCreateStreamOnFileEx(
                file.SourcePath,
                Imapi2Interop.StgmRead | Imapi2Interop.StgmShareDenyWrite,
                0,
                false,
                null,
                out var stream);
            openedStreams.Add(stream);

            target.AddFile(safeName, stream);
            try
            {
                var item = target[safeName];
                if (file.IsHidden) item.IsHidden = true;
                if (FileDateTimeMode == FileDateMode.Current)
                {
                    var now = UseUtcFileDates ? DateTime.UtcNow : DateTime.Now;
                    item.CreationTime = now;
                    item.LastModifiedTime = now;
                }
                else if (FileDateTimeMode == FileDateMode.Custom)
                {
                    item.CreationTime = CustomFileDateTime;
                    item.LastModifiedTime = CustomFileDateTime;
                }
            }
            catch { }
        }

        var folders = AutoSortFiles
            ? source.Folders.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : source.Folders.ToList();

        foreach (var folder in folders)
        {
            string safeName = folder.Name.Length > FileNameMaxLength ? folder.Name[..FileNameMaxLength] : folder.Name;
            if (!folder.IsImported)
            {
                try { target.AddDirectory(safeName); } catch (COMException) { }
            }

            if (target[safeName] is IFsiDirectoryItem subDir)
            {
                if (folder.IsHidden)
                {
                    try { subDir.IsHidden = true; } catch { }
                }
                PopulateFsiTree(folder, subDir, openedStreams);
            }
        }
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

    public static int MapUdfRevisionHex(UdfVersion v) => v switch
    {
        UdfVersion.V1_02 => 0x102,
        UdfVersion.V1_50 => 0x150,
        UdfVersion.V2_00 => 0x200,
        UdfVersion.V2_01 => 0x201,
        UdfVersion.V2_50 => 0x250,
        UdfVersion.V2_60 => 0x260,
        _ => 0x102
    };

    /// <summary>
    /// Maps UdfVersion to legacy numeric .dxp enum values for CDBurnerXP compilation compatibility.
    /// </summary>
    public static int MapUdfVersionLegacyInt(UdfVersion v) => v switch
    {
        UdfVersion.V1_02 => 0,
        UdfVersion.V1_50 => 1,
        UdfVersion.V2_00 => 2,
        UdfVersion.V2_01 => 3,
        UdfVersion.V2_50 => 4,
        UdfVersion.V2_60 => 5,
        _ => 0
    };

    public static UdfVersion MapLegacyIntToUdfVersion(int value) => value switch
    {
        1 => UdfVersion.V1_50,
        2 => UdfVersion.V2_00,
        3 => UdfVersion.V2_01,
        4 => UdfVersion.V2_50,
        5 => UdfVersion.V2_60,
        _ => UdfVersion.V1_02
    };
}
