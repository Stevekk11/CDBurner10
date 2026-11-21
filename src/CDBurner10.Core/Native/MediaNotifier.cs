using System.Runtime.InteropServices;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Utilities;

namespace CDBurner10.Core.Native;

public static class MediaNotifier
{
    public const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public uint dbch_Size;
        public uint dbch_DeviceType;
        public uint dbch_Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_VOLUME
    {
        public int dbcv_Size;
        public int dbcv_Devicetype;
        public int dbcv_Reserved;
        public int dbcv_Unitmask;
        public int dbcv_Flags;
    }

    private static char _watchingDriveLetter = '\0';
    private static DEV_BROADCAST_VOLUME _lastVolumeInfo;
    private static IntPtr _lastEventType;

    public static bool Enabled { get; set; }

    public static string? WatchingDriveLetter
    {
        get => _watchingDriveLetter == '\0' ? null : _watchingDriveLetter.ToString();
        set => _watchingDriveLetter = string.IsNullOrWhiteSpace(value) ? '\0' : char.ToUpperInvariant(value.Trim()[0]);
    }

    public static event Action<string>? MediaInserted;
    public static event Action<string>? MediaRemoved;
    public static event Action<string>? AnyMediaInserted;
    public static event Action<string>? AnyMediaRemoved;
    public static event Action<string, DiscInfoModel>? AnyMediaInfoLoaded;

    public static void ProcessDeviceChangedMessage(IntPtr wParam, IntPtr lParam)
    {
        if (!Enabled || lParam == IntPtr.Zero) return;

        int eventType = wParam.ToInt32();
        if (eventType != DBT_DEVICEARRIVAL && eventType != DBT_DEVICEREMOVECOMPLETE) return;

        var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
        if (hdr.dbch_DeviceType != DBT_DEVTYP_VOLUME) return;

        var vol = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
        if (vol.dbcv_Flags == 2) return; // Network volume

        if (_lastEventType == wParam &&
            _lastVolumeInfo.dbcv_Devicetype == vol.dbcv_Devicetype &&
            _lastVolumeInfo.dbcv_Flags == vol.dbcv_Flags &&
            _lastVolumeInfo.dbcv_Unitmask == vol.dbcv_Unitmask)
        {
            return;
        }

        _lastVolumeInfo = vol;
        _lastEventType = wParam;

        for (char c = 'A'; c <= 'Z'; c++)
        {
            if (PathHelper.IsDriveInUnitMask(vol.dbcv_Unitmask, c))
            {
                if (eventType == DBT_DEVICEARRIVAL)
                {
                    OnAnyMediaInserted(c);
                }
                else
                {
                    AnyMediaRemoved?.Invoke($"{c}:");
                }
            }
        }

        if (_watchingDriveLetter != '\0' && PathHelper.IsDriveInUnitMask(vol.dbcv_Unitmask, _watchingDriveLetter))
        {
            if (eventType == DBT_DEVICEARRIVAL)
                MediaInserted?.Invoke($"{_watchingDriveLetter}:");
            else
                MediaRemoved?.Invoke($"{_watchingDriveLetter}:");
        }
    }

    public static void InvokeMediaInfoRefresh(char driveLetter) => OnAnyMediaInserted(char.ToUpperInvariant(driveLetter));

    private static void OnAnyMediaInserted(char driveLetter)
    {
        string letterStr = $"{driveLetter}:";
        AnyMediaInserted?.Invoke(letterStr);

        _ = Task.Run(() =>
        {
            try
            {
                var drive = BurningEngine.GetDriveByLetter(letterStr);
                if (drive is not null)
                {
                    var discInfo = drive.GetDiscInfo(refresh: true);
                    AnyMediaInfoLoaded?.Invoke(letterStr, discInfo);
                }
            }
            catch
            {
                // Ignore background refresh errors
            }
        });
    }
}
