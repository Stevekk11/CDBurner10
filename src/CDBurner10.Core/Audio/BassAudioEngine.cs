using NAudio.Wave;

namespace CDBurner10.Core.Audio;

/// <summary>
/// Audio subsystem coordinator powered by NAudio, TagLibSharp, FFmpeg, and Win32 CD-DA IOCTLs
/// (replaces closed-source BASS 2.4 while preserving the public BassAudioEngine API).
/// </summary>
public static class BassAudioEngine
{
    private static readonly object SyncLock = new();
    private static bool _isInitialized;
    private static bool _hasSoundDevice;

    public static bool IsInitialized => _isInitialized;
    public static bool HasSoundDevice => _hasSoundDevice;

    /// <summary>
    /// AAC / M4A / MP4 are natively supported via Windows Media Foundation (NAudio) and FFmpeg.
    /// </summary>
    public static bool IsAacSupported => true;

    public static bool Initialize()
    {
        lock (SyncLock)
        {
            if (_isInitialized) return _hasSoundDevice;

            try
            {
                _hasSoundDevice = WaveOut.DeviceCount > 0;
            }
            catch
            {
                _hasSoundDevice = false;
            }

            _isInitialized = true;
            return _hasSoundDevice;
        }
    }

    public static void Shutdown()
    {
        lock (SyncLock)
        {
            _isInitialized = false;
            _hasSoundDevice = false;
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
            prefix + "mp4",
            prefix + "m4b",
            prefix + "m4p",
            prefix + "m4v",
            prefix + "m4r",
            prefix + "3gp",
            prefix + "aac"
        };

        if (includePlaylists)
        {
            list.Add(prefix + "m3u");
            list.Add(prefix + "m3u8");
            list.Add(prefix + "wpl");
            list.Add(prefix + "nra");
            list.Add(prefix + "cue");
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

    public static int GetCdDriveIndexByLetter(string? driveLetter) =>
        CdAudioDevice.GetDriveIndexByLetter(driveLetter);

    public static Dictionary<string, string> GetCdTextDictionary(int cdDriveIndex)
    {
        var letter = CdAudioDevice.GetDriveLetterByIndex(cdDriveIndex);
        return string.IsNullOrWhiteSpace(letter)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : CdAudioDevice.ReadCdText(letter);
    }

    public static string? GetCdTextTrackTitle(string cdaFilePath) =>
        CdAudioDevice.GetCdTextTrackTitle(cdaFilePath);
}
