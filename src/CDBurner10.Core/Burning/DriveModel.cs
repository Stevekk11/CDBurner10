using System.Runtime.InteropServices;
using CDBurner10.Core.Native;

namespace CDBurner10.Core.Burning;

public sealed record DriveSpeedModel(int SpeedKBps, double SpeedRatio)
{
    public string DisplayText => SpeedKBps <= 0
        ? "Maximum"
        : $"{SpeedRatio:0.#}x ({SpeedKBps:N0} KB/s)";

    public override string ToString() => DisplayText;
}

public sealed record TrackModel(
    int TrackNumber,
    bool IsAudio,
    int SizeBlocks,
    int LogicalBlockSize,
    long SizeBytes,
    TimeSpan AudioDuration);

public sealed record SessionModel(
    int SessionNumber,
    IReadOnlyList<TrackModel> Tracks)
{
    public long TotalSizeBytes => Tracks.Sum(t => t.SizeBytes);
}

public sealed record DiscInfoModel(
    OpticalDiscType DiscType,
    string DiscTypeDescription,
    string DiscLabel,
    long TotalSizeBytes,
    long UsedSizeBytes,
    long FreeSizeBytes,
    bool IsBlank,
    bool IsErasable,
    bool IsCd,
    bool IsNextSessionAllowed,
    bool HasDisc,
    int DvdRegionMask,
    IReadOnlyList<SessionModel> Sessions)
{
    public DiscProtectionSystem ProtectionSystem { get; init; }

    public bool IsCssProtected => ProtectionSystem == DiscProtectionSystem.Css;

    public IReadOnlyDictionary<string, bool> RegionInfo => BurningEngine.GetRegionInfo(DvdRegionMask);

    public static DiscInfoModel Empty => new(
        OpticalDiscType.None,
        "No Disc",
        string.Empty,
        0, 0, 0,
        false, false, false, false, false,
        0,
        Array.Empty<SessionModel>())
    {
        ProtectionSystem = DiscProtectionSystem.None
    };
}

public sealed class DriveModel
{
    public string UniqueId { get; }
    public int Index { get; }
    public string Letter { get; }
    public string Vendor { get; }
    public string Product { get; }
    public string Revision { get; }
    public int MaxWriteSpeed { get; }
    public int MaxReadSpeed { get; }
    public OpticalDiscType SupportedMediaRead { get; }
    public OpticalDiscType SupportedMediaWrite { get; }
    public bool SupportTestWrite { get; }
    public bool SupportBufferUnderrunProtection { get; }
    public bool IsWriter => SupportedMediaWrite != OpticalDiscType.None;
    public int State => (!string.IsNullOrWhiteSpace(Letter) && ScsiOpticalDevice.IsUnitReady(Letter)) ? 0 : 1;
    public string DisplayName => $"{Letter}: {Vendor} {Product}".Trim();

    public IReadOnlyList<DriveSpeedModel> SupportedWriteSpeeds { get; private set; } = [new(0, 0)];
    public IReadOnlyList<DriveSpeedModel> SupportedReadSpeeds { get; private set; } = [new(0, 0)];

    public DriveModel(string uniqueId, int index)
    {
        UniqueId = uniqueId;
        Index = index;

        IDiscRecorder2? recorder = null;
        try
        {
            recorder = Imapi2Interop.CreateDiscRecorder2();
            recorder.InitializeDiscRecorder(uniqueId);

            Vendor = (recorder.VendorId ?? string.Empty).Trim();
            Product = (recorder.ProductId ?? string.Empty).Trim();
            Revision = (recorder.ProductRevision ?? string.Empty).Trim();

            string letter = string.Empty;
            try
            {
                var paths = recorder.VolumePathNames;
                if (paths is { Length: > 0 } && paths[0] is string firstPath && !string.IsNullOrWhiteSpace(firstPath))
                {
                    letter = firstPath.Trim().TrimEnd(':', '\\');
                }
            }
            catch (COMException) { }
            Letter = letter;
        }
        finally
        {
            Imapi2Interop.SafeRelease(recorder);
        }

        if (!string.IsNullOrWhiteSpace(Letter))
        {
            var caps = ScsiOpticalDevice.QueryDriveCapabilities(Letter);
            MaxReadSpeed = caps.MaxReadSpeedKBps;
            MaxWriteSpeed = caps.MaxWriteSpeedKBps;
            SupportTestWrite = caps.SupportsTestWrite;
            SupportBufferUnderrunProtection = caps.SupportsBufferUnderrunProtection;
            SupportedMediaRead = caps.SupportedMediaRead;
            SupportedMediaWrite = caps.SupportedMediaWrite;
        }

        RefreshSpeeds();
    }

    internal IDiscRecorder2 OpenRecorder()
    {
        var recorder = Imapi2Interop.CreateDiscRecorder2();
        recorder.InitializeDiscRecorder(UniqueId);
        return recorder;
    }

    public void RefreshSpeeds()
    {
        var writeSpeeds = new List<DriveSpeedModel> { new(0, 0) };
        var readSpeeds = new List<DriveSpeedModel> { new(0, 0) };

        IDiscRecorder2? recorder = null;
        IDiscFormat2Data? format = null;
        try
        {
            recorder = OpenRecorder();
            format = Imapi2Interop.CreateDiscFormat2Data();
            if (format.IsRecorderSupported(recorder) && format.IsCurrentMediaSupported(recorder))
            {
                format.put_Recorder(recorder);
                int mediaType = format.CurrentPhysicalMediaType;
                double base1xKBps = GetBase1xKBps(BurningEngine.MapImapiMediaType(mediaType));

                var rawSpeeds = format.SupportedWriteSpeeds;
                if (rawSpeeds is not null)
                {
                    foreach (var obj in rawSpeeds)
                    {
                        if (obj is int sectorsPerSec && sectorsPerSec > 0)
                        {
                            int kbps = sectorsPerSec * 2; // 2048 bytes/sector = 2 KB/sector
                            double ratio = Math.Round(kbps / base1xKBps, 1);
                            writeSpeeds.Add(new DriveSpeedModel(kbps, ratio));
                        }
                    }
                }
            }
        }
        catch (COMException) { }
        finally
        {
            Imapi2Interop.SafeRelease(format);
            Imapi2Interop.SafeRelease(recorder);
        }

        if (MaxReadSpeed > 0)
        {
            readSpeeds.Add(new DriveSpeedModel(MaxReadSpeed, Math.Round(MaxReadSpeed / 176.4, 1)));
        }

        SupportedWriteSpeeds = writeSpeeds;
        SupportedReadSpeeds = readSpeeds;
    }

    public DiscInfoModel GetDiscInfo(bool refresh = true)
    {
        if (string.IsNullOrWhiteSpace(Letter))
            return DiscInfoModel.Empty;

        if (refresh)
        {
            RefreshSpeeds();
        }

        // First query SCSI profile & layout (works for ALL discs: Audio CD, DVD-ROM, blank, multi-session, unfinalized)
        var discType = ScsiOpticalDevice.QueryCurrentMediaProfile(Letter);
        bool unitReady = ScsiOpticalDevice.IsUnitReady(Letter);
        if (!unitReady && discType == OpticalDiscType.None)
        {
            return DiscInfoModel.Empty;
        }

        var layout = ScsiOpticalDevice.QueryDiscLayout(Letter);
        long totalBytes = layout.TotalBytes;
        long usedBytes = layout.UsedBytes;
        long freeBytes = layout.FreeBytes;
        bool isBlank = layout.IsBlank;
        bool isFinalized = layout.IsFinalized;

        // Refine with IMAPI2 IDiscFormat2Data when writable media is inserted
        IDiscRecorder2? recorder = null;
        IDiscFormat2Data? format = null;
        try
        {
            recorder = OpenRecorder();
            format = Imapi2Interop.CreateDiscFormat2Data();
            if (format.IsCurrentMediaSupported(recorder))
            {
                format.put_Recorder(recorder);
                var imapiType = BurningEngine.MapImapiMediaType(format.CurrentPhysicalMediaType);
                if (imapiType != OpticalDiscType.None) discType = imapiType;

                long imapiTotal = (long)format.TotalSectorsOnMedia * ScsiOpticalDevice.DataSectorSize;
                long imapiFree = (long)format.FreeSectorsOnMedia * ScsiOpticalDevice.DataSectorSize;
                if (imapiTotal > 0)
                {
                    totalBytes = imapiTotal;
                    freeBytes = Math.Max(0, imapiFree);
                    if (usedBytes == 0 && !format.MediaPhysicallyBlank)
                    {
                        usedBytes = Math.Max(0, totalBytes - freeBytes);
                    }
                }

                if (format.MediaPhysicallyBlank || format.MediaHeuristicallyBlank)
                {
                    isBlank = true;
                }

                int status = format.CurrentMediaStatus;
                if ((status & Imapi2Interop.MediaStateFinalized) != 0)
                {
                    isFinalized = true;
                }
                else if ((status & Imapi2Interop.MediaStateAppendable) != 0 || (status & Imapi2Interop.MediaStateBlank) != 0)
                {
                    isFinalized = false;
                }
            }
        }
        catch (COMException) { }
        finally
        {
            Imapi2Interop.SafeRelease(format);
            Imapi2Interop.SafeRelease(recorder);
        }

        if (discType == OpticalDiscType.None && layout.Sessions.Count == 0 && totalBytes == 0)
        {
            return DiscInfoModel.Empty;
        }

        // Read Windows volume label if mounted
        string label = string.Empty;
        try
        {
            var di = new DriveInfo($"{Letter}:\\");
            if (di.IsReady)
            {
                label = di.VolumeLabel ?? string.Empty;
            }
        }
        catch { }

        // Query DVD CSS copy protection & region mask
        var (protection, regionMask) = BurningEngine.IsCdDisc(discType)
            ? (DiscProtectionSystem.None, 0)
            : ScsiOpticalDevice.QueryDvdCopyrightInfo(Letter);

        bool isErasable = layout.IsErasable || BurningEngine.IsDiscErasable(discType);
        bool nextSessionAllowed = !isFinalized;

        return new DiscInfoModel(
            discType,
            BurningEngine.GetDiscDescription(discType),
            label,
            totalBytes,
            usedBytes,
            freeBytes,
            isBlank,
            isErasable,
            BurningEngine.IsCdDisc(discType),
            nextSessionAllowed,
            HasDisc: true,
            regionMask,
            layout.Sessions)
        {
            ProtectionSystem = protection
        };
    }

    public Task<DiscInfoModel> RefreshDiscInfoAsync() => Task.Run(() => GetDiscInfo(refresh: true));

    public void Eject()
    {
        IDiscRecorder2? recorder = null;
        try
        {
            recorder = OpenRecorder();
            recorder.EjectMedia();
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(Letter))
                ScsiOpticalDevice.EjectTray(Letter);
        }
        finally
        {
            Imapi2Interop.SafeRelease(recorder);
        }
    }

    public void Load()
    {
        IDiscRecorder2? recorder = null;
        try
        {
            recorder = OpenRecorder();
            recorder.CloseTray();
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(Letter))
                ScsiOpticalDevice.LoadTray(Letter);
        }
        finally
        {
            Imapi2Interop.SafeRelease(recorder);
        }
    }

    public Task EjectAsync() => Task.Run(() =>
    {
        try { Eject(); } catch { }
    });

    public Task LoadAsync() => Task.Run(() =>
    {
        try { Load(); } catch { }
    });

    private static double GetBase1xKBps(OpticalDiscType mediaType)
    {
        if (BurningEngine.IsCdDisc(mediaType)) return 150.0;
        if ((mediaType & (OpticalDiscType.BdRom | OpticalDiscType.BdR | OpticalDiscType.BdRDl | OpticalDiscType.BdRe)) != 0)
            return 4500.0;
        return 1385.0; // DVD 1x
    }

    public override string ToString() => DisplayName;
}
