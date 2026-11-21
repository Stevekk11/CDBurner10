using System.Runtime.InteropServices;
using CDBurner10.Core.Settings;
using CDBurner10.Core.Utilities;
using RocketDivision.StarBurnX;

namespace CDBurner10.Core.Burning;

public static class BurningEngine
{
    private const string StarBurnRegistrationKey =
        "C0YXRHpEAUhFImo+bjN3D0g1dTl2LmsHTB10MnkHXwVZBEg7YwNMAVcLQTN0NnIvCkMTQmApRRlBPEAOWdGNy5Tw/LGQxqjrpuC26ZjZvOnbpvecpJZc2CTtEu628QnBzKka2yPQEZSr4QLXpPMM3MeowoIe9ZzW7uH0gFzelYilxTmsr43TjZKlRt6W8fX1ob69gy2/DLwwrLmUZcpG4WqAduen0yWOG7G2memSm6Jw9+uw7N9eiKW3FMecLTF+NDbZJUVOuRGpSpdDVQSBP/du9hw3SCMEEVWsU9oEc0wvPypvgwU+dPcGXEHIO/cyoHJVRKhWCQNFPnlY+1R8CCcqkhe+Iqh15xC6CLsqn3WPTglM2lqDJS4D4VfLBEIQvTCuAo8kKRlmPDpnKRt4a25GxgqpDKMF1ms8XUOt1bDWvtOd8owt1KTdlM5/6lmNALR5gC/4Vskp9TPNUKoA8kvUOt2i4uaD18dr5facvZ3Zh+X+R+w1onjBl457mP3zsPzV5Fn7y5BY7sqhKZVX8vzAPfWN9UjNcMADl6HMNavhgybZtP5ErGuz6JvE/ifNk656ytufJ9Ij6GjXzH7MRCc62i/tTgtM6yfHMgIJLyR5AA5iDUVfZAwfm2rHXOQmFmyLWV5HSAULKkMbzQkudSpsMmq9BOBALyY2SZ4/XgmSaixVwxF1ITV6t04hf5sV6D4ZCsZroTHGXEF5KkbqQzQEHH+XOHpuXXv6E6gFQUG/ZsdJxDgNfGRYbja3yTqbYJqNv7XSkcwu5GPWPqggrBrvJ6MRspbgfOgk4DHvr5fytlitOK3lj5KDpLBezfHTUsHzs8is0bkZ+xqGEpfpygOEtOxalebNtZKt9+LUfaXtgeOjxdDIrcfK+KukzLap/7oatRj5CvEKorHHduW23xzGaYIi2nKuyJ5w1nQT71ZRfhpuS1EBGTR0u2BVRxMvoiKFN/p6IXp/acV+nFk8ddxU7ysQa7k07XlFMxk1724TNulqKTTeGAd90m23Ncd2TH3sYyA9YkhXO2BsRiJrcClgPWUpYCBhKWomfCVSGFYhZnAXd0oyS6t4CVNOaJpe6DmrV7Y1XVYIXm5Hs4YX1DKr059Pr5iXRvUMhxW0cZPS+fPQyfIajsbwP/mp7xCwf8di5YWpBPocm+eK3pK44Cud3IugonG7ybJc+0/6aI6c0NrWktEopqH5y+Kzmzaw/bMk/hPbj71l8iThfrDenHSjzLZz63Tu+KjNrjCezJj8s8PKv5vEr2T+7tazG5U2wluYa2FbmioUXw0BPz8KAmoZZ3c+escp+RhqGWhqG3qZLXY2uFwNZWcSQntMZTAgW0eLduxfoi0PZUJXdkYfYPS58rvgIB4fA04FTA==";

    private static readonly object SyncLock = new();
    private static StarBurnX? _starBurn;
    private static STARBURN_TRANSPORT _deviceDriver = STARBURN_TRANSPORT.STARBURN_TRANSPORT_SPTI;
    private static List<DriveModel> _cachedDrives = [];

    public static STARBURN_TRANSPORT DeviceDriver
    {
        get => _deviceDriver;
        set => _deviceDriver = value;
    }

    public static void SetTransport(bool useSptd)
    {
        _deviceDriver = useSptd
            ? STARBURN_TRANSPORT.STARBURN_TRANSPORT_SPTD
            : STARBURN_TRANSPORT.STARBURN_TRANSPORT_SPTI;
    }

    public static string LastLogPath { get; private set; } = string.Empty;
    public static bool IsInitialized => _starBurn is not null;

    public static StarBurnX StarBurnEngine
    {
        get
        {
            if (_starBurn is null)
            {
                Initialize();
            }
            return _starBurn!;
        }
    }

    public static bool CheckSptdDriver()
    {
        try
        {
            var sb = new StarBurnXClass();
            return sb.CheckSPTDDriver(out _, out _);
        }
        catch
        {
            return false;
        }
    }

    public static void Initialize(bool? enableLogging = null, int? logLevel = null, string? logDirectory = null)
    {
        lock (SyncLock)
        {
            if (_starBurn is not null) return;

            var settings = SettingsService.Default.Current;
            bool logging = enableLogging ?? settings.LoggingEnabled;
            int level = logLevel ?? settings.LoggingLevel;

            if (settings.UseSPTD && _deviceDriver == STARBURN_TRANSPORT.STARBURN_TRANSPORT_SPTI)
            {
                if (CheckSptdDriver())
                {
                    _deviceDriver = STARBURN_TRANSPORT.STARBURN_TRANSPORT_SPTD;
                }
            }

            var sb = new StarBurnXClass();
            if (logging)
            {
                string dir = logDirectory ?? SettingsService.Default.SettingsDirectory;
                Directory.CreateDirectory(dir);
                string logFile = Path.Combine(dir, "StarBurn.log");
                PathHelper.TryDeleteFile(logFile);
                LastLogPath = logFile;

                sb.DebugFacility = STARBURN_DEBUG_FACILITIES.STARBURN_DEBUG_FACILITY_FILE;
                sb.DebugLevel = level switch
                {
                    2 => STARBURN_DEBUG_LEVEL.STARBURN_DEBUG_HIGH_LEVEL,
                    0 => STARBURN_DEBUG_LEVEL.STARBURN_DEBUG_LOW_LEVEL,
                    _ => STARBURN_DEBUG_LEVEL.STARBURN_DEBUG_MEDIUM_LEVEL
                };
                sb.DebugFileName = logFile;
            }

            sb.InitializeEx(StarBurnRegistrationKey, _deviceDriver);
            sb.Refresh();
            _starBurn = sb;
            RebuildDriveCache();
        }
    }

    public static void Uninitialize()
    {
        lock (SyncLock)
        {
            if (_starBurn is null) return;
            try
            {
                _starBurn.Uninitialize();
            }
            catch
            {
                // Ignore COM cleanup errors on exit
            }
            finally
            {
                _starBurn = null;
                _cachedDrives = [];
            }
        }
    }

    public static IReadOnlyList<DriveModel> Drives
    {
        get
        {
            lock (SyncLock)
            {
                if (_starBurn is null) Initialize();
                return _cachedDrives;
            }
        }
    }

    public static IReadOnlyList<DriveModel> RefreshDrives()
    {
        lock (SyncLock)
        {
            if (_starBurn is null)
            {
                Initialize();
                return _cachedDrives;
            }

            try
            {
                _starBurn.Refresh();
            }
            catch (COMException)
            {
                // Keep empty list gracefully on 0 drives / bus error
            }
            RebuildDriveCache();
            return _cachedDrives;
        }
    }

    private static void RebuildDriveCache()
    {
        var list = new List<DriveModel>();
        if (_starBurn is null)
        {
            _cachedDrives = list;
            return;
        }

        try
        {
            int count = _starBurn.Drives.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var drive = _starBurn.Drives[i];
                    list.Add(new DriveModel(drive, i));
                }
                catch (COMException)
                {
                    // Skip unreadable drive entry
                }
            }
        }
        catch (COMException)
        {
            // 0 drives or SPTI enumeration unavailable
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

    public static Drive? GetStarBurnDriveByLetter(string? letter) =>
        GetDriveByLetter(letter)?.RawDrive;

    public static bool DeviceLettersEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return char.ToUpperInvariant(a.Trim()[0]) == char.ToUpperInvariant(b.Trim()[0]);
    }

    public static void TryLock(Drive? drive)
    {
        if (drive is null) return;
        try { drive.LockTray(); }
        catch (COMException) { }
    }

    public static void TryUnlock(Drive? drive)
    {
        if (drive is null) return;
        try { drive.UnlockTray(); }
        catch (COMException) { }
    }

    public static bool IsDiscErasable(STARBURN_DISC_TYPE mediaType) =>
        ((int)mediaType & 0xD8C2D48) != 0;

    public static bool IsCdDisc(STARBURN_DISC_TYPE mediaType) =>
        ((int)mediaType & 0x7E) > 0;

    public static string GetDiscDescription(STARBURN_DISC_TYPE mediaType) => mediaType switch
    {
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_CD_R or STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_CD_R_DD => "CD-R",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_CD_RW or STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_CD_RW_DD => "CD-RW",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_CD_ROM or STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_CD_ROM_DD => "CD-ROM",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_R => "DVD-R",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_RW or STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_RW_RO or STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_RW_SR => "DVD-RW",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_PLUS_R => "DVD+R",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_PLUS_R_DL => "DVD+R Dual Layer",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_R_DL => "DVD-R Dual Layer",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_PLUS_RW => "DVD+RW",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_RAM => "DVD-RAM",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_ROM => "DVD-ROM",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_BD_R => "BD-R (Blu-Ray)",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_BD_R_DL => "BD-R (Blu-Ray) Dual Layer",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_BD_RE => "BD-RE (Blu-Ray)",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_BD => "BD-ROM (Blu-Ray)",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_HDDVD_R => "HD-DVD-R",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_HDDVD_R_DL => "HD-DVD-R Dual Layer",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_HDDVD_RW => "HD-DVD-RW",
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_HDDVD_ROM => "HD-DVD-ROM",
        _ => "No Disc"
    };

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
