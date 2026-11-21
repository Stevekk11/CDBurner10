using System.Runtime.InteropServices;

namespace CDBurner10.Core.Audio;

public static class BassAudioEngine
{
    private static readonly object SyncLock = new();
    private static bool _isInitialized;
    private static bool _hasSoundDevice;
    private static bool? _isAacSupported;
    private static readonly List<int> LoadedPluginHandles = [];

    private static readonly string[] CorePlugins =
    [
        "basswma.dll",
        "bassflac.dll",
        "basscd.dll",
        "bassmix.dll",
        "basswv.dll",
        "bass_ape.dll",
        "bass_mpc.dll",
        "bassopus.dll",
        "bass_alac.dll"
    ];

    public static bool IsInitialized => _isInitialized;
    public static bool HasSoundDevice => _hasSoundDevice;

    public static bool IsAacSupported
    {
        get
        {
            if (_isAacSupported.HasValue) return _isAacSupported.Value;
            try
            {
                return File.Exists(Path.Combine(AppContext.BaseDirectory, "bass_aac.dll"));
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool Initialize()
    {
        lock (SyncLock)
        {
            if (_isInitialized) return _hasSoundDevice;

            bool soundAvailable = true;
            try
            {
                int deviceCount = NativeBass.GetDeviceCount();
                if (deviceCount > 1)
                {
                    if (!NativeBass.BASS_Init(1, 44100, NativeBass.BASS_DEVICE_DEFAULT, IntPtr.Zero, IntPtr.Zero))
                    {
                        soundAvailable = false;
                        NativeBass.BASS_Init(0, 44100, NativeBass.BASS_DEVICE_DEFAULT, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                else
                {
                    soundAvailable = false;
                    NativeBass.BASS_Init(0, 44100, NativeBass.BASS_DEVICE_DEFAULT, IntPtr.Zero, IntPtr.Zero);
                }

                var baseDir = AppContext.BaseDirectory;
                foreach (var plugin in CorePlugins)
                {
                    var fullPath = Path.Combine(baseDir, plugin);
                    if (File.Exists(fullPath))
                    {
                        int handle = NativeBass.BASS_PluginLoad(fullPath, NativeBass.BASS_UNICODE);
                        if (handle != 0)
                        {
                            LoadedPluginHandles.Add(handle);
                        }
                    }
                }

                var aacPath = Path.Combine(baseDir, "bass_aac.dll");
                if (File.Exists(aacPath))
                {
                    int aacHandle = NativeBass.BASS_PluginLoad(aacPath, NativeBass.BASS_UNICODE);
                    _isAacSupported = aacHandle != 0;
                    if (aacHandle != 0) LoadedPluginHandles.Add(aacHandle);
                }
                else
                {
                    _isAacSupported = false;
                }

                _isInitialized = true;
                _hasSoundDevice = soundAvailable;
            }
            catch
            {
                _hasSoundDevice = false;
            }

            return _hasSoundDevice;
        }
    }

    public static void Shutdown()
    {
        lock (SyncLock)
        {
            if (!_isInitialized) return;
            try
            {
                foreach (var handle in LoadedPluginHandles)
                {
                    NativeBass.BASS_PluginFree(handle);
                }
                LoadedPluginHandles.Clear();
                NativeBass.BASS_Free();
            }
            catch
            {
                // Ignore shutdown errors
            }
            finally
            {
                _isInitialized = false;
                _hasSoundDevice = false;
            }
        }
    }

    public static IReadOnlyList<string> GetSupportedExtensions(bool includePlaylists = false, bool formatAsFilter = false)
    {
        var prefix = formatAsFilter ? "*." : ".";
        var list = new List<string>
        {
            prefix + "flac",
            prefix + "mpc",
            prefix + "wv",
            prefix + "ape",
            prefix + "mp3",
            prefix + "ogg",
            prefix + "wma",
            prefix + "wav",
            prefix + "aif",
            prefix + "aiff",
            prefix + "mp2",
            prefix + "bwf",
            prefix + "opus",
            prefix + "m4a",
            prefix + "mp4"
        };

        if (includePlaylists)
        {
            list.Add(prefix + "m3u");
            list.Add(prefix + "m3u8");
            list.Add(prefix + "wpl");
            list.Add(prefix + "nra");
            list.Add(prefix + "cue");
        }

        if (IsAacSupported)
        {
            list.Add(prefix + "m4b");
            list.Add(prefix + "m4p");
            list.Add(prefix + "m4v");
            list.Add(prefix + "m4r");
            list.Add(prefix + "3gp");
            list.Add(prefix + "aac");
        }

        return list;
    }

    public static bool IsAudioFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) return false;
        return GetSupportedExtensions(includePlaylists: false).Contains(ext);
    }

    public static int GetCdDriveIndexByLetter(string? driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter)) return -1;
        char target = char.ToUpperInvariant(driveLetter.Trim()[0]);
        try
        {
            int count = NativeBass.GetCdDriveCount();
            for (int i = 0; i < count; i++)
            {
                if (NativeBass.BASS_CD_GetInfo(i, out var info))
                {
                    char letter = (char)('A' + info.letter);
                    if (letter == target) return i;
                }
            }
        }
        catch
        {
            // Ignore if basscd unavailable
        }
        return -1;
    }

    public static Dictionary<string, string> GetCdTextDictionary(int cdDriveIndex)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cdDriveIndex < 0) return result;
        try
        {
            IntPtr ptr = NativeBass.BASS_CD_GetID(cdDriveIndex, NativeBass.BASS_CDID_TEXT);
            if (ptr == IntPtr.Zero) return result;

            int offset = 0;
            while (true)
            {
                string? entry = Marshal.PtrToStringAnsi(IntPtr.Add(ptr, offset));
                if (string.IsNullOrEmpty(entry)) break;
                offset += entry.Length + 1;
                int eq = entry.IndexOf('=');
                if (eq > 0)
                {
                    result[entry[..eq]] = entry[(eq + 1)..];
                }
            }
        }
        catch
        {
            // Ignore CD-Text errors
        }
        return result;
    }

    public static string? GetCdTextTrackTitle(string cdaFilePath)
    {
        try
        {
            var root = Path.GetPathRoot(cdaFilePath);
            int idx = GetCdDriveIndexByLetter(root);
            if (idx < 0) return null;
            var dict = GetCdTextDictionary(idx);
            var nameNoExt = Path.GetFileNameWithoutExtension(cdaFilePath);
            var digits = new string(nameNoExt.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int trackNum) && dict.TryGetValue($"TITLE{trackNum}", out var title))
            {
                return title;
            }
        }
        catch
        {
            // Ignore
        }
        return null;
    }
}
