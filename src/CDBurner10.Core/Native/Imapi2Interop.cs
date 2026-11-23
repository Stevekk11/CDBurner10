using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CDBurner10.Core.Native;

/// <summary>
/// Native Windows IMAPI2 (Image Mastering API v2) COM interop definitions.
/// All vtable slots match the official Windows SDK imapi2.h / imapi2fs.h headers.
/// Built into Windows Vista/7/10/11 (x86, x64, ARM64) — zero external DLLs required.
/// </summary>
internal static class Imapi2Interop
{
    public const int FsiFileSystemNone = 0;
    public const int FsiFileSystemISO9660 = 1;
    public const int FsiFileSystemJoliet = 2;
    public const int FsiFileSystemUDF = 4;

    public const int EmulationNone = 0;
    public const int Emulation12MFloppy = 1;
    public const int Emulation144MFloppy = 2;
    public const int Emulation288MFloppy = 3;
    public const int EmulationHardDisk = 4;

    public const int PlatformX86 = 0;
    public const int PlatformPowerPC = 1;
    public const int PlatformMac = 2;
    public const int PlatformEFI = 0xEF;

    // IMAPI_FORMAT2_DATA_WRITE_ACTION
    public const int WriteActionValidatingMedia = 0;
    public const int WriteActionFormattingMedia = 1;
    public const int WriteActionInitializingHardware = 2;
    public const int WriteActionCalibratingPower = 3;
    public const int WriteActionWritingData = 4;
    public const int WriteActionFinalization = 5;
    public const int WriteActionCompleted = 6;
    public const int WriteActionVerifying = 7;

    // IMAPI_MEDIA_PHYSICAL_TYPE
    public const int MediaTypeUnknown = 0;
    public const int MediaTypeCdRom = 1;
    public const int MediaTypeCdR = 2;
    public const int MediaTypeCdRw = 3;
    public const int MediaTypeDvdRom = 4;
    public const int MediaTypeDvdRam = 5;
    public const int MediaTypeDvdPlusR = 6;
    public const int MediaTypeDvdPlusRw = 7;
    public const int MediaTypeDvdPlusRDualLayer = 8;
    public const int MediaTypeDvdR = 9;
    public const int MediaTypeDvdRw = 10;
    public const int MediaTypeDvdRDualLayer = 11;
    public const int MediaTypeDisk = 12;
    public const int MediaTypeDvdPlusRwDualLayer = 13;
    public const int MediaTypeHdDvdRom = 14;
    public const int MediaTypeHdDvdR = 15;
    public const int MediaTypeHdDvdRam = 16;
    public const int MediaTypeBdRom = 17;
    public const int MediaTypeBdR = 18;
    public const int MediaTypeBdRe = 19;

    // IMAPI_FORMAT2_DATA_MEDIA_STATE flags
    public const int MediaStateUnknown = 0;
    public const int MediaStateOverwriteable = 0x1;
    public const int MediaStateRandomlyWritable = 0x2;
    public const int MediaStateFinalSession = 0x4;
    public const int MediaStateDamaged = 0x8;
    public const int MediaStateAppendable = 0x10;
    public const int MediaStateBlank = 0x20;
    public const int MediaStateFinalized = 0x40;

    // SHCreateStreamOnFileEx mode flags
    public const uint StgmRead = 0x00000000;
    public const uint StgmShareDenyWrite = 0x00000020;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateStreamOnFileEx(
        string pszFile,
        uint grfMode,
        uint dwAttributes,
        bool fCreate,
        IStream? pstmTemplate,
        out IStream ppstm);

    public static IDiscMaster2 CreateDiscMaster2() =>
        (IDiscMaster2)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("2735412E-7F64-5B0F-8F00-5D77AFBE261E"))!)!;

    public static IDiscRecorder2 CreateDiscRecorder2() =>
        (IDiscRecorder2)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("2735412D-7F64-5B0F-8F00-5D77AFBE261E"))!)!;

    public static IDiscFormat2Data CreateDiscFormat2Data() =>
        (IDiscFormat2Data)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("2735412A-7F64-5B0F-8F00-5D77AFBE261E"))!)!;

    public static IDiscFormat2Erase CreateDiscFormat2Erase() =>
        (IDiscFormat2Erase)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("2735412B-7F64-5B0F-8F00-5D77AFBE261E"))!)!;

    public static IDiscFormat2TrackAtOnce CreateDiscFormat2TrackAtOnce() =>
        (IDiscFormat2TrackAtOnce)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("27354129-7F64-5B0F-8F00-5D77AFBE261E"))!)!;

    public static IFileSystemImage CreateFileSystemImage() =>
        (IFileSystemImage)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("2C941FC5-975B-59BE-A960-9A2A262853A5"))!)!;

    public static IBootOptions CreateBootOptions() =>
        (IBootOptions)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("2C941FCE-975B-59BE-A960-9A2A262853A5"))!)!;

    public static void SafeRelease(object? comObj)
    {
        if (comObj is not null && Marshal.IsComObject(comObj))
        {
            try { Marshal.ReleaseComObject(comObj); } catch { }
        }
    }

    public static IDisposable AdviseEventSink(object comSource, Guid eventInterfaceId, object sink)
    {
        var cpc = (IConnectionPointContainer)comSource;
        cpc.FindConnectionPoint(ref eventInterfaceId, out var cp);
        if (cp is null) return NoOpDisposable.Instance;
        cp.Advise(sink, out int cookie);
        return new ConnectionPointSubscription(cp, cookie);
    }

    public static void CopyIStreamToFile(IStream source, string outputPath, long totalBytes, Action<int>? onPercent, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var buffer = new byte[1024 * 1024];
        IntPtr readPtr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            source.Seek(0, 0, IntPtr.Zero);
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan);
            long written = 0;
            int lastPct = -1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                source.Read(buffer, buffer.Length, readPtr);
                int bytesRead = Marshal.ReadInt32(readPtr);
                if (bytesRead <= 0) break;
                fs.Write(buffer, 0, bytesRead);
                written += bytesRead;
                if (totalBytes > 0 && onPercent is not null)
                {
                    int pct = (int)Math.Clamp(written * 100L / totalBytes, 0, 100);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        onPercent(pct);
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(readPtr);
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class ConnectionPointSubscription(IConnectionPoint cp, int cookie) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { cp.Unadvise(cookie); } catch { }
            SafeRelease(cp);
        }
    }
}

// =========================================================================
// Core IMAPI2 COM Interfaces (exact Windows SDK vtable slot order)
// =========================================================================

[ComImport, Guid("27354130-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IDiscMaster2
{
    IEnumerator GetEnumerator();
    string this[int index] { [return: MarshalAs(UnmanagedType.BStr)] get; }
    int Count { get; }
    bool IsSupportedEnvironment { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
}

[ComImport, Guid("27354133-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IDiscRecorder2
{
    void EjectMedia();
    void CloseTray();
    void AcquireExclusiveAccess([MarshalAs(UnmanagedType.VariantBool)] bool force, [MarshalAs(UnmanagedType.BStr)] string clientName);
    void ReleaseExclusiveAccess();
    void DisableMcn();
    void EnableMcn();
    void InitializeDiscRecorder([MarshalAs(UnmanagedType.BStr)] string recorderUniqueId);

    string ActiveDiscRecorder { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string VendorId { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string ProductId { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string ProductRevision { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string VolumeName { [return: MarshalAs(UnmanagedType.BStr)] get; }
    object[] VolumePathNames { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    bool DeviceCanLoadMedia { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    int LegacyDeviceNumber { get; }
    object[] SupportedFeaturePages { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    object[] CurrentFeaturePages { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    object[] SupportedProfiles { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    object[] CurrentProfiles { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    object[] SupportedModePages { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    string ExclusiveAccessOwner { [return: MarshalAs(UnmanagedType.BStr)] get; }
}

[ComImport, Guid("27354153-9F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IDiscFormat2Data
{
    // IDiscFormat2 base
    [return: MarshalAs(UnmanagedType.VariantBool)] bool IsRecorderSupported(IDiscRecorder2 recorder);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool IsCurrentMediaSupported(IDiscRecorder2 recorder);
    bool MediaPhysicallyBlank { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    bool MediaHeuristicallyBlank { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    object[] SupportedMediaTypes { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    // IDiscFormat2Data (note put_ precedes get_ in imapi2.h)
    void put_Recorder(IDiscRecorder2 value);
    IDiscRecorder2 get_Recorder();
    void put_BufferUnderrunFreeDisabled([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_BufferUnderrunFreeDisabled();
    void put_PostgapAlreadyInImage([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_PostgapAlreadyInImage();
    int CurrentMediaStatus { get; }
    int WriteProtectStatus { get; }
    int TotalSectorsOnMedia { get; }
    int FreeSectorsOnMedia { get; }
    int NextWritableAddress { get; }
    int StartAddressOfPreviousSession { get; }
    int LastWrittenAddressOfPreviousSession { get; }
    void put_ForceMediaToBeClosed([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_ForceMediaToBeClosed();
    void put_DisableConsumerDvdCompatibilityMode([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_DisableConsumerDvdCompatibilityMode();
    int CurrentPhysicalMediaType { get; }
    void put_ClientName([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string get_ClientName();
    int RequestedWriteSpeed { get; }
    bool RequestedRotationTypeIsPureCAV { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    int CurrentWriteSpeed { get; }
    bool CurrentRotationTypeIsPureCAV { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    object[] SupportedWriteSpeeds { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    object[] SupportedWriteSpeedDescriptors { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    void put_ForceOverwrite([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_ForceOverwrite();
    object[] MultisessionInterfaces { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    void Write(IStream data);
    void CancelWrite();
    void SetWriteSpeed(int requestedSectorsPerSecond, [MarshalAs(UnmanagedType.VariantBool)] bool rotationTypeIsPureCAV);
}

[ComImport, Guid("2735413D-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IDiscFormat2DataEventArgs
{
    int StartLba { get; }
    int SectorCount { get; }
    int LastReadLba { get; }
    int LastWrittenLba { get; }
    int TotalSystemBuffer { get; }
    int UsedSystemBuffer { get; }
    int FreeSystemBuffer { get; }
    int ElapsedTime { get; }
    int RemainingTime { get; }
    int TotalTime { get; }
    int CurrentAction { get; }
}

[ComImport, Guid("2735413C-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface DDiscFormat2DataEvents
{
    [DispId(0x200)]
    void Update([MarshalAs(UnmanagedType.IDispatch)] object sender, [MarshalAs(UnmanagedType.IDispatch)] object progress);
}

[ComImport, Guid("27354156-8F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IDiscFormat2Erase
{
    // IDiscFormat2 base
    [return: MarshalAs(UnmanagedType.VariantBool)] bool IsRecorderSupported(IDiscRecorder2 recorder);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool IsCurrentMediaSupported(IDiscRecorder2 recorder);
    bool MediaPhysicallyBlank { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    bool MediaHeuristicallyBlank { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    object[] SupportedMediaTypes { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    // IDiscFormat2Erase (put_ precedes get_)
    void put_Recorder(IDiscRecorder2 value);
    IDiscRecorder2 get_Recorder();
    void put_FullErase([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_FullErase();
    int CurrentPhysicalMediaType { get; }
    void put_ClientName([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string get_ClientName();
    void EraseMedia();
}

[ComImport, Guid("2735413A-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface DDiscFormat2EraseEvents
{
    [DispId(0x200)]
    void Update([MarshalAs(UnmanagedType.IDispatch)] object sender, int elapsedSeconds, int estimatedTotalSeconds);
}

[ComImport, Guid("27354154-8F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IDiscFormat2TrackAtOnce
{
    // IDiscFormat2 base
    [return: MarshalAs(UnmanagedType.VariantBool)] bool IsRecorderSupported(IDiscRecorder2 recorder);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool IsCurrentMediaSupported(IDiscRecorder2 recorder);
    bool MediaPhysicallyBlank { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    bool MediaHeuristicallyBlank { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    object[] SupportedMediaTypes { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    // IDiscFormat2TrackAtOnce
    void PrepareMedia();
    void AddAudioTrack(IStream data);
    void CancelAddTrack();
    void ReleaseMedia();
    void SetWriteSpeed(int requestedSectorsPerSecond, [MarshalAs(UnmanagedType.VariantBool)] bool rotationTypeIsPureCAV);
    void put_Recorder(IDiscRecorder2 value);
    IDiscRecorder2 get_Recorder();
    void put_BufferUnderrunFreeDisabled([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_BufferUnderrunFreeDisabled();
    int NumberOfExistingTracks { get; }
    int TotalSectorsOnMedia { get; }
    int FreeSectorsOnMedia { get; }
    int UsedSectorsOnMedia { get; }
    void put_DoNotFinalizeMedia([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_DoNotFinalizeMedia();
    object[] ExpectedTableOfContents { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    int CurrentPhysicalMediaType { get; }
    void put_ClientName([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string get_ClientName();
    int RequestedWriteSpeed { get; }
    bool RequestedRotationTypeIsPureCAV { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    int CurrentWriteSpeed { get; }
    bool CurrentRotationTypeIsPureCAV { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    object[] SupportedWriteSpeeds { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    object[] SupportedWriteSpeedDescriptors { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
}

[ComImport, Guid("27354140-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IDiscFormat2TrackAtOnceEventArgs
{
    int StartLba { get; }
    int SectorCount { get; }
    int LastReadLba { get; }
    int LastWrittenLba { get; }
    int TotalSystemBuffer { get; }
    int UsedSystemBuffer { get; }
    int FreeSystemBuffer { get; }
    int CurrentTrackNumber { get; }
    int CurrentAction { get; }
    int ElapsedTime { get; }
    int RemainingTime { get; }
}

[ComImport, Guid("2735413F-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface DDiscFormat2TrackAtOnceEvents
{
    [DispId(0x200)]
    void Update([MarshalAs(UnmanagedType.IDispatch)] object sender, [MarshalAs(UnmanagedType.IDispatch)] object progress);
}

// =========================================================================
// IMAPI2FS Interfaces (exact Windows SDK imapi2fs.h vtable slot order)
// =========================================================================

[ComImport, Guid("2C941FD4-975B-59BE-A960-9A2A262853A5"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IBootOptions
{
    IStream BootImage { get; }
    string Manufacturer { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
    int PlatformId { get; set; }
    int Emulation { get; set; }
    uint ImageSize { get; }
    void AssignBootImage(IStream newVal);
}

[ComImport, Guid("2C941FD8-975B-59BE-A960-9A2A262853A5"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IFileSystemImageResult
{
    IStream ImageStream { get; }
    object ProgressItems { get; }
    int TotalBlocks { get; }
    int BlockSize { get; }
    string DiscId { [return: MarshalAs(UnmanagedType.BStr)] get; }
}

[ComImport, Guid("2C941FD9-975B-59BE-A960-9A2A262853A5"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IFsiItem
{
    string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string FullPath { [return: MarshalAs(UnmanagedType.BStr)] get; }
    DateTime CreationTime { get; set; }
    DateTime LastAccessedTime { get; set; }
    DateTime LastModifiedTime { get; set; }
    bool IsHidden { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    [return: MarshalAs(UnmanagedType.BStr)] string FileSystemName(int fileSystem);
    [return: MarshalAs(UnmanagedType.BStr)] string FileSystemPath(int fileSystem);
}

[ComImport, Guid("2C941FDB-975B-59BE-A960-9A2A262853A5"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IFsiFileItem : IFsiItem
{
    new string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
    new string FullPath { [return: MarshalAs(UnmanagedType.BStr)] get; }
    new DateTime CreationTime { get; set; }
    new DateTime LastAccessedTime { get; set; }
    new DateTime LastModifiedTime { get; set; }
    new bool IsHidden { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    [return: MarshalAs(UnmanagedType.BStr)] new string FileSystemName(int fileSystem);
    [return: MarshalAs(UnmanagedType.BStr)] new string FileSystemPath(int fileSystem);

    long DataSize { get; }
    int DataSize32BitLow { get; }
    int DataSize32BitHigh { get; }
    IStream Data { get; set; }
}

[ComImport, Guid("2C941FDC-975B-59BE-A960-9A2A262853A5"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IFsiDirectoryItem : IFsiItem
{
    new string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
    new string FullPath { [return: MarshalAs(UnmanagedType.BStr)] get; }
    new DateTime CreationTime { get; set; }
    new DateTime LastAccessedTime { get; set; }
    new DateTime LastModifiedTime { get; set; }
    new bool IsHidden { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    [return: MarshalAs(UnmanagedType.BStr)] new string FileSystemName(int fileSystem);
    [return: MarshalAs(UnmanagedType.BStr)] new string FileSystemPath(int fileSystem);

    IEnumerator GetEnumerator();
    IFsiItem this[[MarshalAs(UnmanagedType.BStr)] string path] { get; }
    int Count { get; }
    object EnumFsiItems { get; }
    void AddDirectory([MarshalAs(UnmanagedType.BStr)] string path);
    void AddFile([MarshalAs(UnmanagedType.BStr)] string path, IStream fileData);
    void AddTree([MarshalAs(UnmanagedType.BStr)] string sourceDirectory, [MarshalAs(UnmanagedType.VariantBool)] bool includeBaseDirectory);
    void Add(IFsiItem item);
    void Remove([MarshalAs(UnmanagedType.BStr)] string path);
    void RemoveTree([MarshalAs(UnmanagedType.BStr)] string path);
}

[ComImport, Guid("2C941FE1-975B-59BE-A960-9A2A262853A5"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IFileSystemImage
{
    IFsiDirectoryItem Root { get; }
    int SessionStartBlock { get; set; }
    int FreeMediaBlocks { get; set; }
    void SetMaxMediaBlocksFromDevice(IDiscRecorder2 discRecorder);
    int UsedBlocks { get; }
    string VolumeName { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
    string ImportedVolumeName { [return: MarshalAs(UnmanagedType.BStr)] get; }
    IBootOptions? BootImageOptions { get; set; }
    int FileCount { get; }
    int DirectoryCount { get; }
    string WorkingDirectory { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
    int ChangePoint { get; }
    bool StrictFileSystemCompliance { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    bool UseRestrictedCharacterSet { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    int FileSystemsToCreate { get; set; }
    int FileSystemsSupported { get; }
    // Note: put_UDFRevision precedes get_UDFRevision in imapi2fs.h
    void put_UDFRevision(int value);
    int get_UDFRevision();
    object[] UDFRevisionsSupported { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    void ChooseImageDefaults(IDiscRecorder2 discRecorder);
    void ChooseImageDefaultsForMediaType(int value);
    // Note: put_ISO9660InterchangeLevel precedes get_ISO9660InterchangeLevel in imapi2fs.h
    void put_ISO9660InterchangeLevel(int value);
    int get_ISO9660InterchangeLevel();
    object[] ISO9660InterchangeLevelsSupported { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
    IFileSystemImageResult CreateResultImage();
    int Exists([MarshalAs(UnmanagedType.BStr)] string fullPath);
    [return: MarshalAs(UnmanagedType.BStr)] string CalculateDiscIdentifier();
    int IdentifyFileSystemsOnDisc(IDiscRecorder2? discRecorder);
    int GetDefaultFileSystemForImport(int fileSystems);
    int ImportFileSystem();
    void ImportSpecificFileSystem(int fileSystemToUse);
    void RollbackToChangePoint(int changePoint);
    void LockInChangePoint();
    IFsiDirectoryItem CreateDirectoryItem([MarshalAs(UnmanagedType.BStr)] string name);
    IFsiFileItem CreateFileItem([MarshalAs(UnmanagedType.BStr)] string name);
    string VolumeNameUDF { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string VolumeNameJoliet { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string VolumeNameISO9660 { [return: MarshalAs(UnmanagedType.BStr)] get; }
    bool StageFiles { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    object[] MultisessionInterfaces { [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; [param: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] set; }
}

// =========================================================================
// Event Sink Implementations (CCW for IConnectionPoint.Advise)
// =========================================================================

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class DiscFormat2DataEventSink(Action<IDiscFormat2DataEventArgs> onUpdate) : DDiscFormat2DataEvents
{
    public void Update(object sender, object progress)
    {
        if (progress is IDiscFormat2DataEventArgs args)
        {
            onUpdate(args);
        }
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class DiscFormat2EraseEventSink(Action<int, int> onUpdate) : DDiscFormat2EraseEvents
{
    public void Update(object sender, int elapsedSeconds, int estimatedTotalSeconds)
    {
        onUpdate(elapsedSeconds, estimatedTotalSeconds);
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class DiscFormat2TrackAtOnceEventSink(Action<IDiscFormat2TrackAtOnceEventArgs> onUpdate) : DDiscFormat2TrackAtOnceEvents
{
    public void Update(object sender, object progress)
    {
        if (progress is IDiscFormat2TrackAtOnceEventArgs args)
        {
            onUpdate(args);
        }
    }
}

/// <summary>
/// Wraps a managed Stream as a COM IStream for IMAPI2 (e.g. raw PCM audio slice from a WAV file).
/// </summary>
[ComVisible(true)]
internal sealed class ManagedComStream(Stream stream, bool leaveOpen = true) : IStream, IDisposable
{
    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        int read = stream.Read(pv, 0, cb);
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten)
    {
        stream.Write(pv, 0, cb);
        if (pcbWritten != IntPtr.Zero) Marshal.WriteInt32(pcbWritten, cb);
    }

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        var origin = dwOrigin switch
        {
            0 => SeekOrigin.Begin,
            1 => SeekOrigin.Current,
            2 => SeekOrigin.End,
            _ => SeekOrigin.Begin
        };
        long pos = stream.Seek(dlibMove, origin);
        if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, pos);
    }

    public void SetSize(long libNewSize) => stream.SetLength(libNewSize);

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        var buffer = new byte[Math.Min(cb, 65536)];
        long remaining = cb;
        long totalRead = 0;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, toRead);
            if (read <= 0) break;
            pstm.Write(buffer, read, IntPtr.Zero);
            totalRead += read;
            remaining -= read;
        }
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, totalRead);
        if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, totalRead);
    }

    public void Commit(int grfCommitFlags) => stream.Flush();
    public void Revert() { }
    public void LockRegion(long libOffset, long cb, int dwLockType) { }
    public void UnlockRegion(long libOffset, long cb, int dwLockType) { }

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new STATSTG
        {
            type = 2, // STGTY_STREAM
            cbSize = stream.Length,
            grfMode = 0
        };
    }

    public void Clone(out IStream ppstm) => throw new NotSupportedException();

    public void Dispose()
    {
        if (!leaveOpen) stream.Dispose();
    }
}
