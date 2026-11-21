using System.Runtime.InteropServices;
using RocketDivision.StarBurnX;

namespace CDBurner10.Core.Burning;

public sealed class GenericDataBurner
{
    private const long MaxIsoFileSizeBytes = 4L * 1024 * 1024 * 1024 - 1; // 4 GiB - 1

    private DataFileSystem _fileSystem = DataFileSystem.IsoJoliet;
    private DataBurnerClass? _isoBurner;
    private UDFDataBurnerClass? _udfBurner;
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

    public GenericDataBurner()
    {
    }

    private void EnsureBurnerInstance()
    {
        if (_fileSystem == DataFileSystem.IsoJoliet)
        {
            _isoBurner ??= new DataBurnerClass();
        }
        else
        {
            _udfBurner ??= new UDFDataBurnerClass();
        }
    }

    public void SetFileSystemType(DataFileSystem fileSystem)
    {
        if (_fileSystem == fileSystem) return;

        if (_fileSystem == DataFileSystem.Udf && fileSystem == DataFileSystem.IsoJoliet)
        {
            // Check if any file exceeds 4 GiB
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
        EnsureBurnerInstance();
        try
        {
            if (_fileSystem == DataFileSystem.IsoJoliet)
            {
                _isoBurner!.Drive = drive.RawDrive;
                _isoBurner.ImportTrack(trackNumber);
                PopulateVirtualTreeFromStarBurn(_isoBurner.Files, RootFolder);
            }
            else
            {
                _udfBurner!.Drive = drive.RawDrive;
                _udfBurner.ImportTrack(trackNumber);
                PopulateVirtualTreeFromStarBurn(_udfBurner.Files, RootFolder);
            }
            _isImported = true;
        }
        catch (COMException ex) when (ex.IsError(STARBURN_RESULT_CODES.STARBURN_ERR_UNSUPPORTED_DISC_FILE_SYSTEM))
        {
            // Auto-switch between ISO/Joliet and UDF
            _fileSystem = _fileSystem == DataFileSystem.Udf ? DataFileSystem.IsoJoliet : DataFileSystem.Udf;
            EnsureBurnerInstance();
            if (_fileSystem == DataFileSystem.IsoJoliet)
            {
                _isoBurner!.Drive = drive.RawDrive;
                _isoBurner.ImportTrack(trackNumber);
                PopulateVirtualTreeFromStarBurn(_isoBurner.Files, RootFolder);
            }
            else
            {
                _udfBurner!.Drive = drive.RawDrive;
                _udfBurner.ImportTrack(trackNumber);
                PopulateVirtualTreeFromStarBurn(_udfBurner.Files, RootFolder);
            }
            _isImported = true;
        }
    }

    private static void PopulateVirtualTreeFromStarBurn(DataFiles files, VirtualFolder target)
    {
        foreach (IDataFile item in files)
        {
            if (item is IDataFolder folder)
            {
                var vf = target.FindFolder(folder.Name) ?? new VirtualFolder(folder.Name, target) { IsImported = true, IsHidden = folder.GetIsHidden() };
                if (!target.Folders.Contains(vf)) target.Folders.Add(vf);
                PopulateVirtualTreeFromStarBurn(folder.Files, vf);
            }
            else
            {
                if (target.FindFile(item.Name) is null)
                {
                    target.Files.Add(new VirtualFile
                    {
                        Name = item.Name,
                        SourcePath = item.Path ?? string.Empty,
                        FileSizeBytes = (long)item.Size * 1024L,
                        IsHidden = item.GetIsHidden(),
                        IsImported = item.ItemType == STARBURN_FS_ITEM_TYPE.STARBURN_FS_ITEM_IMPORTED_FROM_TRACK,
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

                string publisher = options.PublisherName ?? PublisherName;
                string appName = options.ApplicationName ?? ApplicationName;

                if (_fileSystem == DataFileSystem.IsoJoliet)
                {
                    BurnIsoInternal(options, label, publisher, appName, progress);
                }
                else
                {
                    BurnUdfInternal(options, label, progress);
                }

                if (options.EjectAfterBurn && !_isCancelled)
                {
                    try { options.Drive.RawDrive.Eject(); }
                    catch (COMException) { }
                }
            }

            progress?.Report(new BurnProgress(
                _isCancelled ? BurnPhase.Cancelled : BurnPhase.Completed,
                100,
                Message: _isCancelled ? "Burn cancelled." : "Burn completed successfully."));
        }, ct);
    }

    private void BurnIsoInternal(
        DataBurnOptions options,
        string label,
        string publisher,
        string appName,
        IProgress<BurnProgress>? progress)
    {
        var burner = new DataBurnerClass();
        _isoBurner = burner;
        ConfigureCommonBurner(burner, options);
        burner.IsXACDROM = IsXaCdRom;
        burner.AutoSortFiles = AutoSortFiles;
        burner.DisableCollisionsDetection = DisableCollisionsDetection;
        PopulateStarBurnTree(RootFolder, burner);

        int lastBuf = 0;
        burner.OnBuffer += p => { lastBuf = p; };
        burner.OnProgress += (p, rem) => progress?.Report(new BurnProgress(BurnPhase.Burning, p, lastBuf, TimeSpan.FromSeconds(rem), Message: $"Writing data... {p}%"));
        burner.OnCloseSessionBegin += () => progress?.Report(new BurnProgress(BurnPhase.ClosingSession, 99, lastBuf, Message: "Closing session..."));
        burner.OnVerify += (p, rem) => progress?.Report(new BurnProgress(BurnPhase.Verifying, p, 0, TimeSpan.FromSeconds(rem), Message: $"Verifying... {p}%"));

        try
        {
            burner.Burn(options.TestBurn, label, publisher, appName);
            if (options.VerifyData && !_isCancelled)
            {
                burner.Verify();
            }
        }
        catch (COMException ex) when ((ex.ErrorCode & 0xFFF) == 640 && burner.Mode == STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_TAO)
        {
            burner.Mode = STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_AUTO;
            burner.Burn(options.TestBurn, label, publisher, appName);
            if (options.VerifyData && !_isCancelled)
            {
                burner.Verify();
            }
        }
        finally
        {
            try { burner.UnlockFileSystem(); } catch { }
        }
    }

    private void BurnUdfInternal(
        DataBurnOptions options,
        string label,
        IProgress<BurnProgress>? progress)
    {
        var burner = new UDFDataBurnerClass();
        _udfBurner = burner;
        ConfigureCommonUdfBurner(burner, options);
        PopulateStarBurnTree(RootFolder, burner);

        int lastBuf = 0;
        burner.OnBuffer += p => { lastBuf = p; };
        burner.OnProgress += (p, rem) => progress?.Report(new BurnProgress(BurnPhase.Burning, p, lastBuf, TimeSpan.FromSeconds(rem), Message: $"Writing UDF data... {p}%"));
        burner.OnCloseSessionBegin += () => progress?.Report(new BurnProgress(BurnPhase.ClosingSession, 99, lastBuf, Message: "Closing session..."));
        burner.OnVerify += (p, rem) => progress?.Report(new BurnProgress(BurnPhase.Verifying, p, 0, TimeSpan.FromSeconds(rem), Message: $"Verifying... {p}%"));

        try
        {
            burner.Burn(options.TestBurn, label);
            if (options.VerifyData && !_isCancelled)
            {
                burner.Verify();
            }
        }
        catch (COMException ex) when ((ex.ErrorCode & 0xFFF) == 640 && burner.Mode == STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_TAO)
        {
            burner.Mode = STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_AUTO;
            burner.Burn(options.TestBurn, label);
            if (options.VerifyData && !_isCancelled)
            {
                burner.Verify();
            }
        }
        finally
        {
            try { burner.UnlockFileSystem(); } catch { }
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
            using var reg = ct.Register(Cancel);

            var dir = Path.GetDirectoryName(outputIsoPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string rawLabel = !string.IsNullOrWhiteSpace(volumeName) ? volumeName : VolumeName;
            string label = DataCompilation.ReplaceDiscLabelVariables(rawLabel);
            if (label.Length > NameMaxLength) label = label[..NameMaxLength];

            progress?.Report(new BurnProgress(BurnPhase.Preparing, 0, Message: "Building ISO image..."));

            if (_fileSystem == DataFileSystem.IsoJoliet)
            {
                var burner = new DataBurnerClass
                {
                    AutoResolveCollisions = true,
                    IsXACDROM = IsXaCdRom,
                    AutoSortFiles = AutoSortFiles,
                    DisableCollisionsDetection = DisableCollisionsDetection,
                    FileDateTimeMode = MapDateMode(FileDateTimeMode),
                    FileDateTimeFormat = UseUtcFileDates
                        ? STARBURN_DATETIME_FORMAT.STARBURN_DATETIME_FORMAT_UTC
                        : STARBURN_DATETIME_FORMAT.STARBURN_DATETIME_FORMAT_LOCAL
                };
                if (FileDateTimeMode == FileDateMode.Custom) burner.SetCustomFileDateTime(CustomFileDateTime);
                ApplyBootImage(burner);
                _isoBurner = burner;
                PopulateStarBurnTree(RootFolder, burner);
                burner.OnProgress += (p, rem) => progress?.Report(new BurnProgress(BurnPhase.Burning, p, 0, TimeSpan.FromSeconds(rem), Message: $"Saving ISO... {p}%"));
                try
                {
                    burner.SaveImageFile(outputIsoPath, label, PublisherName, ApplicationName);
                }
                finally
                {
                    try { burner.UnlockFileSystem(); } catch { }
                }
            }
            else
            {
                var burner = new UDFDataBurnerClass
                {
                    AutoResolveCollisions = true,
                    FileSystemVersion = MapUdfVersion(UdfVersion),
                    FileDateTimeMode = MapDateMode(FileDateTimeMode),
                    FileDateTimeFormat = UseUtcFileDates
                        ? STARBURN_DATETIME_FORMAT.STARBURN_DATETIME_FORMAT_UTC
                        : STARBURN_DATETIME_FORMAT.STARBURN_DATETIME_FORMAT_LOCAL
                };
                if (FileDateTimeMode == FileDateMode.Custom) burner.SetCustomFileDateTime(CustomFileDateTime);
                ApplyBootImage(burner);
                _udfBurner = burner;
                PopulateStarBurnTree(RootFolder, burner);
                burner.OnProgress += (p, rem) => progress?.Report(new BurnProgress(BurnPhase.Burning, p, 0, TimeSpan.FromSeconds(rem), Message: $"Saving UDF ISO... {p}%"));
                try
                {
                    burner.SaveImageFile(outputIsoPath, label);
                }
                finally
                {
                    try { burner.UnlockFileSystem(); } catch { }
                }
            }

            progress?.Report(new BurnProgress(BurnPhase.Completed, 100, Message: "ISO saved successfully."));
        }, ct);
    }

    public void Cancel()
    {
        _isCancelled = true;
        _ = Task.Run(() =>
        {
            try { _isoBurner?.Cancel(); } catch { }
            try { _udfBurner?.Cancel(); } catch { }
        });
    }

    private void ConfigureCommonBurner(DataBurnerClass burner, DataBurnOptions options)
    {
        burner.Drive = options.Drive.RawDrive;
        burner.AutoResolveCollisions = true;
        burner.Mode = MapWriteMode(options.Mode);
        burner.NextSessionAllowed = !options.FinalizeDisc;
        if (options.FinalizeDisc) burner.KeepSessionOpened = false;
        burner.CheckOverburning = CheckOverburning;
        burner.FileDateTimeMode = MapDateMode(FileDateTimeMode);
        burner.FileDateTimeFormat = UseUtcFileDates
            ? STARBURN_DATETIME_FORMAT.STARBURN_DATETIME_FORMAT_UTC
            : STARBURN_DATETIME_FORMAT.STARBURN_DATETIME_FORMAT_LOCAL;
        if (FileDateTimeMode == FileDateMode.Custom) burner.SetCustomFileDateTime(CustomFileDateTime);
        ApplyBootImage(burner);
        if (options.WriteSpeedKBps > 0)
        {
            try { burner.WriteSpeed = options.WriteSpeedKBps; } catch (COMException) { }
        }
    }

    private void ConfigureCommonUdfBurner(UDFDataBurnerClass burner, DataBurnOptions options)
    {
        burner.Drive = options.Drive.RawDrive;
        burner.AutoResolveCollisions = true;
        burner.FileSystemVersion = MapUdfVersion(UdfVersion);
        burner.Mode = MapWriteMode(options.Mode);
        burner.NextSessionAllowed = !options.FinalizeDisc;
        if (options.FinalizeDisc) burner.KeepSessionOpened = false;
        burner.CheckOverburning = CheckOverburning;
        burner.FileDateTimeMode = MapDateMode(FileDateTimeMode);
        burner.FileDateTimeFormat = UseUtcFileDates
            ? STARBURN_DATETIME_FORMAT.STARBURN_DATETIME_FORMAT_UTC
            : STARBURN_DATETIME_FORMAT.STARBURN_DATETIME_FORMAT_LOCAL;
        if (FileDateTimeMode == FileDateMode.Custom) burner.SetCustomFileDateTime(CustomFileDateTime);
        ApplyBootImage(burner);
        if (options.WriteSpeedKBps > 0)
        {
            try { burner.WriteSpeed = options.WriteSpeedKBps; } catch (COMException) { }
        }
    }

    private void ApplyBootImage(DataBurnerClass burner)
    {
        if (BootImage.Enabled && !string.IsNullOrWhiteSpace(BootImage.FileName) && File.Exists(BootImage.FileName))
        {
            var bi = new BootImageClass
            {
                FileName = BootImage.FileName,
                SystemType = BootImage.Platform,
                LoadSegment = BootImage.LoadSegment,
                Type = BootImage.EmulationType,
                SectorsToLoad = BootImage.SectorsToLoad
            };
            burner.BootImage = bi;
        }
        else
        {
            burner.BootImage = null;
        }
    }

    private void ApplyBootImage(UDFDataBurnerClass burner)
    {
        if (BootImage.Enabled && !string.IsNullOrWhiteSpace(BootImage.FileName) && File.Exists(BootImage.FileName))
        {
            var bi = new BootImageClass
            {
                FileName = BootImage.FileName,
                SystemType = BootImage.Platform,
                LoadSegment = BootImage.LoadSegment,
                Type = BootImage.EmulationType,
                SectorsToLoad = BootImage.SectorsToLoad
            };
            burner.BootImage = bi;
        }
        else
        {
            burner.BootImage = null;
        }
    }

    private void PopulateStarBurnTree(VirtualFolder source, IDataFolder target)
    {
        foreach (var file in source.Files)
        {
            if (file.IsImported || string.IsNullOrWhiteSpace(file.SourcePath) || !File.Exists(file.SourcePath))
                continue;

            string safeName = file.Name.Length > FileNameMaxLength ? file.Name[..FileNameMaxLength] : file.Name;
            var added = target.AddFileEx(file.SourcePath, safeName);
            if (added is not null && file.IsHidden)
            {
                added.SetIsHidden(true);
            }
        }

        foreach (var folder in source.Folders)
        {
            string safeName = folder.Name.Length > FileNameMaxLength ? folder.Name[..FileNameMaxLength] : folder.Name;
            var created = target.CreateFolder(safeName);
            if (created is not null)
            {
                if (folder.IsHidden) created.SetIsHidden(true);
                PopulateStarBurnTree(folder, created);
            }
        }
    }

    public static STARBURN_UDF_VERSION MapUdfVersion(UdfVersion v) => v switch
    {
        UdfVersion.V1_02 => STARBURN_UDF_VERSION.STARBURN_UDF_VERSION_1_02,
        UdfVersion.V1_50 => STARBURN_UDF_VERSION.STARBURN_UDF_VERSION_1_50,
        UdfVersion.V2_00 => STARBURN_UDF_VERSION.STARBURN_UDF_VERSION_2_00,
        UdfVersion.V2_01 => STARBURN_UDF_VERSION.STARBURN_UDF_VERSION_2_01,
        UdfVersion.V2_50 => STARBURN_UDF_VERSION.STARBURN_UDF_VERSION_2_50,
        UdfVersion.V2_60 => STARBURN_UDF_VERSION.STARBURN_UDF_VERSION_2_60,
        _ => STARBURN_UDF_VERSION.STARBURN_UDF_VERSION_1_02
    };

    public static UdfVersion MapStarBurnUdfVersion(STARBURN_UDF_VERSION v) => v switch
    {
        STARBURN_UDF_VERSION.STARBURN_UDF_VERSION_1_50 => UdfVersion.V1_50,
        STARBURN_UDF_VERSION.STARBURN_UDF_VERSION_2_00 => UdfVersion.V2_00,
        STARBURN_UDF_VERSION.STARBURN_UDF_VERSION_2_01 => UdfVersion.V2_01,
        STARBURN_UDF_VERSION.STARBURN_UDF_VERSION_2_50 => UdfVersion.V2_50,
        STARBURN_UDF_VERSION.STARBURN_UDF_VERSION_2_60 => UdfVersion.V2_60,
        _ => UdfVersion.V1_02
    };

    public static STARBURN_WRITE_MODE MapWriteMode(WriteMode mode) => mode switch
    {
        WriteMode.TAO => STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_TAO,
        WriteMode.SAO => STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_SAO,
        WriteMode.DAO96 => STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_DAO_96,
        _ => STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_AUTO
    };

    private static STARBURN_FILE_DATETIME_MODE MapDateMode(FileDateMode mode) => mode switch
    {
        FileDateMode.Current => STARBURN_FILE_DATETIME_MODE.STARBURN_FILE_DATETIME_CURRENT,
        FileDateMode.Custom => STARBURN_FILE_DATETIME_MODE.STARBURN_FILE_DATETIME_CUSTOM,
        _ => STARBURN_FILE_DATETIME_MODE.STARBURN_FILE_DATETIME_ORIGINAL
    };
}
