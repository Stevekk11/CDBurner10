using System.Runtime.InteropServices;
using CDBurner10.Core.Native;

namespace CDBurner10.Core.Burning;

/// <summary>
/// Manages optical drive discovery and shared disc utilities via Windows IMAPI2 and SPTI.
/// Zero closed-source dependencies.
/// </summary>
public static class BurningEngine
{
    private static readonly object SyncLock = new();
    private static bool _isInitialized;
    private static List<DriveModel> _cachedDrives = [];

    public static string LastLogPath { get; private set; } = string.Empty;
    public static bool IsInitialized => _isInitialized;

    public static void Initialize(bool? enableLogging = null, int? logLevel = null, string? logDirectory = null)
    {
        lock (SyncLock)
        {
            if (_isInitialized) return;
            _isInitialized = true;
            RebuildDriveCache();
        }
    }

    public static void Uninitialize()
    {
        lock (SyncLock)
        {
            _isInitialized = false;
            _cachedDrives = [];
        }
    }

    public static IReadOnlyList<DriveModel> Drives
    {
        get
        {
            lock (SyncLock)
            {
                if (!_isInitialized) Initialize();
                return _cachedDrives;
            }
        }
    }

    public static IReadOnlyList<DriveModel> RefreshDrives()
    {
        lock (SyncLock)
        {
            if (!_isInitialized)
            {
                Initialize();
                return _cachedDrives;
            }

            RebuildDriveCache();
            return _cachedDrives;
        }
    }

    private static void RebuildDriveCache()
    {
        var list = new List<DriveModel>();
        IDiscMaster2? master = null;
        try
        {
            master = Imapi2Interop.CreateDiscMaster2();
            if (!master.IsSupportedEnvironment)
            {
                _cachedDrives = list;
                return;
            }

            int count = master.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    string uniqueId = master[i];
                    if (string.IsNullOrWhiteSpace(uniqueId)) continue;
                    list.Add(new DriveModel(uniqueId, i));
                }
                catch (COMException)
                {
                    // Skip unreadable drive entry
                }
            }
        }
        catch (COMException)
        {
            // 0 optical drives or IMAPI2 service unavailable
        }
        catch (Exception)
        {
            // Keep graceful empty list on non-optical environments
        }
        finally
        {
            Imapi2Interop.SafeRelease(master);
        }

        _cachedDrives = list;
    }

    public static DriveModel? GetDrive(int index)
    {
        var drives = Drives;
        return index >= 0 && index < drives.Count ? drives[index] : null;
    }

    public static DriveModel? GetDriveByLetter(string? letter)
    {
        if (string.IsNullOrWhiteSpace(letter)) return null;
        foreach (var d in Drives)
        {
            if (DeviceLettersEqual(d.Letter, letter)) return d;
        }
        return null;
    }

    public static DriveModel? GetDrive(string? indexOrLetter)
    {
        if (string.IsNullOrWhiteSpace(indexOrLetter)) return null;
        var trimmed = indexOrLetter.Trim().TrimEnd(':', '\\');

        var byLetter = GetDriveByLetter(trimmed);
        if (byLetter is not null) return byLetter;

        if (int.TryParse(trimmed, out int idx))
        {
            return GetDrive(idx);
        }

        return null;
    }

    public static bool DeviceLettersEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return char.ToUpperInvariant(a.Trim()[0]) == char.ToUpperInvariant(b.Trim()[0]);
    }

    public static void TryLock(DriveModel? drive)
    {
        if (drive is null || string.IsNullOrWhiteSpace(drive.Letter)) return;
        ScsiOpticalDevice.SetTrayLock(drive.Letter, true);
    }

    public static void TryUnlock(DriveModel? drive)
    {
        if (drive is null || string.IsNullOrWhiteSpace(drive.Letter)) return;
        ScsiOpticalDevice.SetTrayLock(drive.Letter, false);
    }

    public static bool IsDiscErasable(OpticalDiscType mediaType) =>
        (mediaType & (OpticalDiscType.CdRw | OpticalDiscType.DvdRw | OpticalDiscType.DvdPlusRw |
                      OpticalDiscType.DvdRam | OpticalDiscType.BdRe | OpticalDiscType.HdDvdRw)) != 0;

    public static bool IsCdDisc(OpticalDiscType mediaType) =>
        (mediaType & (OpticalDiscType.CdRom | OpticalDiscType.CdR | OpticalDiscType.CdRw)) != 0;

    public static OpticalDiscType MapImapiMediaType(int imapiPhysicalType) => imapiPhysicalType switch
    {
        Imapi2Interop.MediaTypeCdRom => OpticalDiscType.CdRom,
        Imapi2Interop.MediaTypeCdR => OpticalDiscType.CdR,
        Imapi2Interop.MediaTypeCdRw => OpticalDiscType.CdRw,
        Imapi2Interop.MediaTypeDvdRom => OpticalDiscType.DvdRom,
        Imapi2Interop.MediaTypeDvdRam => OpticalDiscType.DvdRam,
        Imapi2Interop.MediaTypeDvdPlusR => OpticalDiscType.DvdPlusR,
        Imapi2Interop.MediaTypeDvdPlusRw or Imapi2Interop.MediaTypeDvdPlusRwDualLayer => OpticalDiscType.DvdPlusRw,
        Imapi2Interop.MediaTypeDvdPlusRDualLayer => OpticalDiscType.DvdPlusRDl,
        Imapi2Interop.MediaTypeDvdR => OpticalDiscType.DvdR,
        Imapi2Interop.MediaTypeDvdRw => OpticalDiscType.DvdRw,
        Imapi2Interop.MediaTypeDvdRDualLayer => OpticalDiscType.DvdRDl,
        Imapi2Interop.MediaTypeHdDvdRom => OpticalDiscType.HdDvdRom,
        Imapi2Interop.MediaTypeHdDvdR => OpticalDiscType.HdDvdR,
        Imapi2Interop.MediaTypeHdDvdRam => OpticalDiscType.HdDvdRw,
        Imapi2Interop.MediaTypeBdRom => OpticalDiscType.BdRom,
        Imapi2Interop.MediaTypeBdR => OpticalDiscType.BdR,
        Imapi2Interop.MediaTypeBdRe => OpticalDiscType.BdRe,
        _ => OpticalDiscType.None
    };

    public static string GetDiscDescription(OpticalDiscType mediaType) => mediaType switch
    {
        OpticalDiscType.CdR => "CD-R",
        OpticalDiscType.CdRw => "CD-RW",
        OpticalDiscType.CdRom => "CD-ROM",
        OpticalDiscType.DvdR => "DVD-R",
        OpticalDiscType.DvdRw => "DVD-RW",
        OpticalDiscType.DvdPlusR => "DVD+R",
        OpticalDiscType.DvdPlusRDl => "DVD+R Dual Layer",
        OpticalDiscType.DvdRDl => "DVD-R Dual Layer",
        OpticalDiscType.DvdPlusRw => "DVD+RW",
        OpticalDiscType.DvdRam => "DVD-RAM",
        OpticalDiscType.DvdRom => "DVD-ROM",
        OpticalDiscType.BdR => "BD-R (Blu-Ray)",
        OpticalDiscType.BdRDl => "BD-R (Blu-Ray) Dual Layer",
        OpticalDiscType.BdRe => "BD-RE (Blu-Ray)",
        OpticalDiscType.BdRom => "BD-ROM (Blu-Ray)",
        OpticalDiscType.HdDvdR => "HD-DVD-R",
        OpticalDiscType.HdDvdRDl => "HD-DVD-R Dual Layer",
        OpticalDiscType.HdDvdRw => "HD-DVD-RW",
        OpticalDiscType.HdDvdRom => "HD-DVD-ROM",
        _ => "No Disc"
    };

    public static TimeSpan GetAudioDuration(int logicalBlocks) =>
        TimeSpan.FromSeconds(logicalBlocks / 75.0);

    public static IReadOnlyDictionary<string, bool> GetRegionInfo(int regionMask) =>
        new Dictionary<string, bool>
        {
            ["Region 1: USA, Canada"] = (regionMask & 0x01) == 0,
            ["Region 2: Europe, Japan, South Africa"] = (regionMask & 0x02) == 0,
            ["Region 3: Southeast & East Asia"] = (regionMask & 0x04) == 0,
            ["Region 4: Australia, New Zealand, Latin America"] = (regionMask & 0x08) == 0,
            ["Region 5: Africa, India, Eastern Europe, Russia"] = (regionMask & 0x10) == 0,
            ["Region 6: China"] = (regionMask & 0x20) == 0,
            ["Region 8: Airlines / Cruise Ships"] = (regionMask & 0x80) == 0
        };
}
