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
    TimeSpan AudioDuration,
    int StartLba = 0);

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
    public int MaxWriteSpeed { get; private set; }
    public int MaxReadSpeed { get; private set; }
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

        OpticalDiscType imapiReadMask = OpticalDiscType.None;
        OpticalDiscType imapiWriteMask = OpticalDiscType.None;
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

            try
            {
                foreach (var obj in recorder.SupportedProfiles ?? [])
                {
                    if (obj is int p && p > 0)
                    {
                        var mapped = ScsiOpticalDevice.MapProfileCode((ushort)p);
                        if (mapped != OpticalDiscType.None)
                        {
                            imapiReadMask |= mapped;
                            if ((mapped & (OpticalDiscType.CdRom | OpticalDiscType.DvdRom | OpticalDiscType.BdRom | OpticalDiscType.HdDvdRom)) == 0)
                            {
                                imapiWriteMask |= mapped;
                            }
                        }
                    }
                }
            }
            catch (COMException) { }
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
            SupportedMediaRead = imapiReadMask | caps.SupportedMediaRead;
            SupportedMediaWrite = imapiWriteMask | caps.SupportedMediaWrite;
        }
        else
        {
            SupportedMediaRead = imapiReadMask;
            SupportedMediaWrite = imapiWriteMask;
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
        var detectedMedia = OpticalDiscType.None;

        IDiscRecorder2? recorder = null;
        IDiscFormat2Data? format = null;
        try
        {
            recorder = OpenRecorder();
            try
            {
                foreach (var obj in recorder.CurrentProfiles ?? [])
                {
                    if (obj is int p && p > 0)
                    {
                        var mapped = ScsiOpticalDevice.MapProfileCode((ushort)p);
                        if (mapped != OpticalDiscType.None)
                        {
                            detectedMedia = mapped;
                            break;
                        }
                    }
                }
            }
            catch (COMException) { }

            format = Imapi2Interop.CreateDiscFormat2Data();
            if (format.IsRecorderSupported(recorder) && format.IsCurrentMediaSupported(recorder))
            {
                format.put_Recorder(recorder);
                int mediaType = format.CurrentPhysicalMediaType;
                var mappedMedia = BurningEngine.MapImapiMediaType(mediaType);
                if (mappedMedia != OpticalDiscType.None)
                    detectedMedia = mappedMedia;
                double base1xKBps = GetBase1xKBps(mappedMedia);

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

        if (!string.IsNullOrWhiteSpace(Letter))
        {
            int liveRead = ScsiOpticalDevice.QueryMaxReadSpeedKBps(Letter);
            if (liveRead > 0) MaxReadSpeed = liveRead;
        }

        SupportedWriteSpeeds = writeSpeeds;
        SupportedReadSpeeds = BuildReadSpeedPresets(MaxReadSpeed, detectedMedia);
    }

    public static IReadOnlyList<DriveSpeedModel> BuildReadSpeedPresets(int maxReadKBps, OpticalDiscType mediaType = OpticalDiscType.None)
    {
        var list = new List<DriveSpeedModel> { new(0, 0) };

        bool isCd = BurningEngine.IsCdDisc(mediaType) ||
                    (mediaType == OpticalDiscType.None && maxReadKBps > 0 && maxReadKBps <= 10500);
        bool isBd = (mediaType & (OpticalDiscType.BdRom | OpticalDiscType.BdR | OpticalDiscType.BdRDl | OpticalDiscType.BdRe)) != 0 ||
                    (mediaType == OpticalDiscType.None && maxReadKBps > 25000);

        double base1xKBps;
        int[] multipliers;
        if (isCd)
        {
            base1xKBps = 176.4;
            multipliers = [52, 48, 40, 32, 24, 16, 12, 8, 4, 2, 1];
        }
        else if (isBd)
        {
            base1xKBps = 4500.0;
            multipliers = [16, 12, 8, 6, 4, 2, 1];
        }
        else
        {
            base1xKBps = 1385.0;
            multipliers = [24, 20, 16, 12, 8, 6, 4, 2, 1];
        }

        int maxRealisticKBps = (int)Math.Round(multipliers[0] * base1xKBps);
        int minFloorKBps = (int)Math.Round((isCd ? 48 : isBd ? 12 : 16) * base1xKBps);
        int capKBps = Math.Min(Math.Max(maxReadKBps, minFloorKBps), maxRealisticKBps);

        foreach (int mult in multipliers)
        {
            int kbps = (int)Math.Round(mult * base1xKBps);
            if (kbps > capKBps + 5)
                continue;
            list.Add(new DriveSpeedModel(kbps, mult));
        }

        // If the drive reports a specific non-standard ceiling (e.g. 10x Silent Play), include it sorted
        if (maxReadKBps > 0 && maxReadKBps <= maxRealisticKBps &&
            !list.Any(s => s.SpeedKBps > 0 && Math.Abs(s.SpeedKBps - maxReadKBps) < base1xKBps * 0.4))
        {
            list.Add(new DriveSpeedModel(maxReadKBps, Math.Round(maxReadKBps / base1xKBps, 1)));
        }

        return [list[0], .. list.Skip(1).OrderByDescending(s => s.SpeedKBps)];
    }

    public DiscInfoModel GetDiscInfo(bool refresh = true)
    {
        if (string.IsNullOrWhiteSpace(Letter))
            return DiscInfoModel.Empty;

        if (refresh)
        {
            RefreshSpeeds();
        }

        var discType = OpticalDiscType.None;
        bool imapiHasCssFeature = false;
        long totalBytes = 0;
        long usedBytes = 0;
        long freeBytes = 0;
        bool isBlank = false;
        bool isFinalized = false;

        // 1. Query IMAPI2 CurrentProfiles + CurrentFeaturePages (works for ALL discs, including DVD-ROM/CD-ROM, without Admin)
        IDiscRecorder2? recorder = null;
        IDiscFormat2Data? format = null;
        try
        {
            recorder = OpenRecorder();
            try
            {
                foreach (var obj in recorder.CurrentProfiles ?? [])
                {
                    if (obj is int p && p > 0)
                    {
                        var mapped = ScsiOpticalDevice.MapProfileCode((ushort)p);
                        if (mapped != OpticalDiscType.None)
                        {
                            discType = mapped;
                            break;
                        }
                    }
                }
            }
            catch (COMException) { }

            try
            {
                foreach (var obj in recorder.CurrentFeaturePages ?? [])
                {
                    if (obj is int f && f == 0x0106) // Feature 0x0106 = DVD CSS
                    {
                        imapiHasCssFeature = true;
                        break;
                    }
                }
            }
            catch (COMException) { }

            // Refine writable media info via IMAPI2 IDiscFormat2Data
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
                    if (!format.MediaPhysicallyBlank)
                    {
                        usedBytes = Math.Max(0, totalBytes - freeBytes);
                    }
                }

                if (format.MediaPhysicallyBlank || format.MediaHeuristicallyBlank)
                {
                    isBlank = true;
                }

                int status = format.CurrentMediaStatus;
                if ((status & Imapi2Interop.MediaStateFinalized) != 0 && freeBytes == 0)
                {
                    isFinalized = true;
                }
                else if ((status & Imapi2Interop.MediaStateAppendable) != 0 ||
                         (status & Imapi2Interop.MediaStateBlank) != 0 ||
                         freeBytes > 0)
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

        // 2. Supplement with SCSI SPTI layout + profile (gives detailed multi-session/track info when available)
        if (discType == OpticalDiscType.None)
        {
            discType = ScsiOpticalDevice.QueryCurrentMediaProfile(Letter);
        }

        var layout = ScsiOpticalDevice.QueryDiscLayout(Letter);
        IReadOnlyList<SessionModel> sessions = layout.Sessions;
        if (sessions.Count == 0 && !isBlank)
        {
            sessions = ScsiOpticalDevice.QuerySessionsViaReadTocEx(Letter);
        }

        if (layout.IsBlank && totalBytes == 0) isBlank = true;
        if (layout.IsFinalized) isFinalized = true;
        if (usedBytes == 0 && layout.UsedBytes > 0) usedBytes = layout.UsedBytes;
        if (freeBytes == 0 && layout.FreeBytes > 0) freeBytes = layout.FreeBytes;
        if (totalBytes == 0 && layout.TotalBytes > 0) totalBytes = layout.TotalBytes;

        // 3. Read Windows volume info (works for all mounted CD-ROM / DVD-ROM / BD-ROM discs without Admin)
        string label = string.Empty;
        bool volumeReady = false;
        try
        {
            var di = new DriveInfo($"{Letter}:\\");
            if (di.IsReady)
            {
                volumeReady = true;
                label = di.VolumeLabel ?? string.Empty;
                if (totalBytes == 0 && di.TotalSize > 0)
                {
                    totalBytes = di.TotalSize;
                    freeBytes = Math.Max(0, di.TotalFreeSpace);
                    usedBytes = Math.Max(0, totalBytes - freeBytes);
                }
            }
        }
        catch { }

        if (totalBytes == 0 && (discType != OpticalDiscType.None || volumeReady))
        {
            long rawLen = ScsiOpticalDevice.QueryDeviceLengthBytes(Letter);
            if (rawLen > 0)
            {
                totalBytes = rawLen;
                if (!isBlank && usedBytes == 0) usedBytes = rawLen;
            }
        }

        if (usedBytes == 0 && sessions.Count > 0)
        {
            usedBytes = sessions.Sum(s => s.TotalSizeBytes);
            if (totalBytes < usedBytes) totalBytes = usedBytes;
        }

        bool hasDisc = discType != OpticalDiscType.None || volumeReady || sessions.Count > 0 || totalBytes > 0;
        if (!hasDisc)
        {
            return DiscInfoModel.Empty;
        }

        if (discType == OpticalDiscType.None && volumeReady)
        {
            discType = totalBytes > 900L * 1024 * 1024 ? OpticalDiscType.DvdRom : OpticalDiscType.CdRom;
        }

        if (discType is OpticalDiscType.CdRom or OpticalDiscType.DvdRom or OpticalDiscType.BdRom or OpticalDiscType.HdDvdRom)
        {
            isFinalized = true;
        }

        // 4. Query DVD CSS copy protection & region mask
        var (protection, regionMask) = BurningEngine.IsCdDisc(discType)
            ? (DiscProtectionSystem.None, 0)
            : ScsiOpticalDevice.QueryDvdCopyrightInfo(Letter);

        if (protection == DiscProtectionSystem.None && imapiHasCssFeature)
        {
            protection = DiscProtectionSystem.Css;
        }

        bool isErasable = layout.IsErasable || BurningEngine.IsDiscErasable(discType);
        bool nextSessionAllowed = !isFinalized;
        SupportedReadSpeeds = BuildReadSpeedPresets(MaxReadSpeed, discType);

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
            sessions)
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
