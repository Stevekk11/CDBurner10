using System.Globalization;
using System.Text.Json;

namespace CDBurner10.Core.Settings;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly object DefaultLock = new();
    private static SettingsService? _default;
    public static SettingsService Default
    {
        get
        {
            if (_default is null)
            {
                lock (DefaultLock)
                {
                    if (_default is null)
                    {
                        var svc = new SettingsService();
                        svc.Load();
                        _default = svc;
                    }
                }
            }
            return _default;
        }
    }

    public AppSettings Current { get; private set; } = new();
    public bool IsPortableMode { get; }
    public string SettingsDirectory { get; }
    public string SettingsFilePath { get; }

    public SettingsService(string? customSettingsDirectory = null)
    {
        if (!string.IsNullOrEmpty(customSettingsDirectory))
        {
            SettingsDirectory = customSettingsDirectory;
            IsPortableMode = false;
        }
        else
        {
            IsPortableMode = DetectPortableMode();
            SettingsDirectory = IsPortableMode
                ? AppContext.BaseDirectory
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CDBurner10");
        }

        SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    Current = loaded;
                    return Current;
                }
            }

            // One-time legacy UserSettings.ini import
            var legacyIniPath = FindLegacyIniPath();
            if (legacyIniPath is not null && File.Exists(legacyIniPath))
            {
                Current = ImportLegacyIni(legacyIniPath);
                Save();
                return Current;
            }
        }
        catch
        {
            // Fall back to defaults on corrupted settings
        }

        Current = new AppSettings();
        return Current;
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }

    public void ResetToDefaults()
    {
        Current = new AppSettings();
        Save();
    }

    public string GetTempDirectory()
    {
        if (Current.UseCustomTempFolder && !string.IsNullOrWhiteSpace(Current.CustomTempFolder))
        {
            try
            {
                Directory.CreateDirectory(Current.CustomTempFolder);
                return Current.CustomTempFolder;
            }
            catch
            {
                // Fallback to system temp
            }
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "CDBurner10");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    public string GetTempRipDirectory()
    {
        var dir = Path.Combine(GetTempDirectory(), "Rip");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetTempFileName(string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return Path.Combine(GetTempDirectory(), $"{Guid.NewGuid():N}{ext}");
    }

    public string GetTempRipFileName(string desiredBaseName, string extension = ".wav")
    {
        var safe = Utilities.PathHelper.ReplaceInvalidFileNameChars(desiredBaseName);
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "Track";
        }
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var dir = GetTempRipDirectory();
        var candidate = Path.Combine(dir, safe + ext);
        int suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{safe}_{suffix++}{ext}");
        }
        return candidate;
    }

    private static bool DetectPortableMode()
    {
        var baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "portable.flag")))
        {
            return true;
        }

        var configIni = Path.Combine(baseDir, "Config.ini");
        if (!File.Exists(configIni))
        {
            return false;
        }

        try
        {
            var ini = ParseIni(configIni);
            if (ini.TryGetValue("CDBurnerXP", out var section) &&
                section.TryGetValue("Portable", out var val) &&
                (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch
        {
            // Ignore malformed Config.ini
        }

        return false;
    }

    private string? FindLegacyIniPath()
    {
        if (IsPortableMode)
        {
            var portableIni = Path.Combine(AppContext.BaseDirectory, "UserSettings.ini");
            return File.Exists(portableIni) ? portableIni : null;
        }

        var roamingIni = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Canneverbe Limited",
            "CDBurnerXP",
            "UserSettings.ini");
        return File.Exists(roamingIni) ? roamingIni : null;
    }

    public static AppSettings ImportLegacyIni(string iniPath)
    {
        var settings = new AppSettings();
        var ini = ParseIni(iniPath);

        string GetStr(string section, string key, string fallback) =>
            ini.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : fallback;

        bool GetBool(string section, string key, bool fallback)
        {
            var raw = GetStr(section, key, string.Empty);
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (raw == "0" || raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        int GetInt(string section, string key, int fallback) =>
            int.TryParse(GetStr(section, key, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        double GetDouble(string section, string key, double fallback) =>
            double.TryParse(GetStr(section, key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

        // [General]
        settings.AppLanguage = GetStr("General", "AppLanguage", settings.AppLanguage);
        settings.ExitToNotify = GetBool("General", "ExitToNotify", settings.ExitToNotify);
        settings.ToolbarDisplayStyle = GetInt("General", "ToolbarDisplayStyle", settings.ToolbarDisplayStyle);
        settings.OnlyUseSingleUnit = GetBool("General", "OnlyUseSingleUnit", settings.OnlyUseSingleUnit);
        settings.StartLastUsedProjectType = GetBool("General", "StartLastUsedProjectType", settings.StartLastUsedProjectType);
        settings.LastUsedProjectType = GetStr("General", "LastUsedProjectType", settings.LastUsedProjectType);
        settings.LoggingEnabled = GetBool("General", "LoggingEnabled", settings.LoggingEnabled);
        settings.LoggingLevel = GetInt("General", "LoggingLevel", settings.LoggingLevel);
        settings.UseCustomTempFolder = GetBool("General", "UseCustomTempFolder", settings.UseCustomTempFolder);
        settings.CustomTempFolder = GetStr("General", "CustomTempFolder", settings.CustomTempFolder);
        settings.PreventDefaultDragDrop = GetBool("General", "PreventDefaultDragDrop", settings.PreventDefaultDragDrop);
        settings.UseFastFolderSynchronisation = GetBool("General", "UseFastFolderSynchronisation", settings.UseFastFolderSynchronisation);

        // [Data]
        settings.DataExplorerPath = GetStr("Data", "DataExplorerPath", settings.DataExplorerPath);
        settings.DataExplorer = GetBool("Data", "DataExplorer", settings.DataExplorer);
        settings.IgnoreFileRenameRequirements = GetBool("Data", "IgnoreFileRenameRequirements", settings.IgnoreFileRenameRequirements);
        settings.DataCacheFilesAutomatically = GetBool("Data", "DataCacheFilesAutomatically", settings.DataCacheFilesAutomatically);
        settings.DataCacheNetworkFilesAutomatically = GetBool("Data", "DataCacheNetworkFilesAutomatically", settings.DataCacheNetworkFilesAutomatically);
        settings.DataUpdateAutomatically = GetBool("Data", "DataUpdateAutomatically", settings.DataUpdateAutomatically);
        settings.DataBurnVerify = GetBool("Data", "DataBurnVerify", settings.DataBurnVerify);
        settings.DataBurnFinalize = GetBool("Data", "DataBurnFinalize", settings.DataBurnFinalize);
        settings.DataUseDiscAtOnce = GetBool("Data", "DataUseDiscAtOnce", settings.DataUseDiscAtOnce);
        settings.DataBurnEject = GetBool("Data", "DataBurnEject", settings.DataBurnEject);
        settings.DataTileVertical = GetBool("Data", "DataTileVertical", settings.DataTileVertical);
        settings.DataMode2XA = GetBool("Data", "DataMode2XA", settings.DataMode2XA);
        settings.DataAskForSettings = GetBool("Data", "DataAskForSettings", settings.DataAskForSettings);
        settings.DataFilePattern = GetStr("Data", "DataFilePattern", settings.DataFilePattern);
        settings.DataFilePatternInclude = GetBool("Data", "DataFilePatternInclude", settings.DataFilePatternInclude);
        settings.DataFilePatternShowInBrowser = GetBool("Data", "DataFilePatternShowInBrowser", settings.DataFilePatternShowInBrowser);
        settings.DataSaveISOPath = GetStr("Data", "DataSaveISOPath", settings.DataSaveISOPath);
        settings.DataVideoLayoutPath = GetStr("Data", "DataVideoLayoutPath", settings.DataVideoLayoutPath);
        settings.DataExplorerViewStyle = GetInt("Data", "DataExplorerViewStyle", settings.DataExplorerViewStyle);
        settings.LastDataBurnSpeed = GetInt("Data", "LastDataBurnSpeed", settings.LastDataBurnSpeed);
        settings.DataFileSystemType = GetInt("Data", "DataFileSystemType", settings.DataFileSystemType);
        settings.DataUdfVersion = GetInt("Data", "DataUdfVersion", settings.DataUdfVersion);
        settings.DataIsoLevel = GetInt("Data", "DataIsoLevel", settings.DataIsoLevel);
        settings.DataLastDeviceUsed = GetStr("Data", "DataLastDeviceUsed", settings.DataLastDeviceUsed);
        settings.DataFloppyImagePath = GetStr("Data", "DataFloppyImagePath", settings.DataFloppyImagePath);
        settings.DataCopyImagePath = GetStr("Data", "DataCopyImagePath", settings.DataCopyImagePath);
        settings.CopySourceDevice = GetStr("Data", "CopySourceDevice", settings.CopySourceDevice);
        settings.CopyDisableHardwareEC = GetBool("Data", "CopyDisableHardwareEC", settings.CopyDisableHardwareEC);
        settings.CopyIgnoreUnreadableData = GetBool("Data", "CopyIgnoreUnreadableData", settings.CopyIgnoreUnreadableData);
        settings.CopyReadRetries = GetInt("Data", "CopyReadRetries", settings.CopyReadRetries);
        settings.CopyImageType = GetInt("Data", "CopyImageType", settings.CopyImageType);
        settings.CopyEjectDisc = GetBool("Data", "CopyEjectDisc", settings.CopyEjectDisc);
        settings.CopyFinaliseDisc = GetBool("Data", "CopyFinaliseDisc", settings.CopyFinaliseDisc);
        settings.DataConvertTargetPath = GetStr("Data", "DataConvertTargetPath", settings.DataConvertTargetPath);
        settings.DataSaveCompilationPath = GetStr("Data", "DataSaveCompilationPath", settings.DataSaveCompilationPath);
        settings.DataLabel = GetStr("Data", "DataLabel", settings.DataLabel);
        settings.DataCachePath = GetStr("Data", "DataCachePath", settings.DataCachePath);
        settings.DoubleLayerCompatible = GetBool("Data", "DoubleLayerCompatible", settings.DoubleLayerCompatible);
        settings.PadDvdSize = GetBool("Data", "PadDvdSize", settings.PadDvdSize);
        settings.ModifyIfoBupFiles = GetBool("Data", "ModifyIfoBupFiles", settings.ModifyIfoBupFiles);
        settings.VideoLastDeviceUsed = GetStr("Data", "VideoLastDeviceUsed", settings.VideoLastDeviceUsed);

        // [Iso]
        settings.IsoEjectDisc = GetBool("Iso", "IsoEjectDisc", settings.IsoEjectDisc);
        settings.IsoFinalize = GetBool("Iso", "IsoFinalize", settings.IsoFinalize);
        settings.IsoMode2XA = GetBool("Iso", "IsoMode2XA", settings.IsoMode2XA);
        settings.IsoLastDeviceUsed = GetStr("Iso", "IsoLastDeviceUsed", settings.IsoLastDeviceUsed);
        settings.LastUsedIsoPath = GetStr("Iso", "LastUsedIsoPath", settings.LastUsedIsoPath);

        // [Audio]
        settings.AudioExplorerPath = GetStr("Audio", "AudioExplorerPath", settings.AudioExplorerPath);
        settings.AudioAutoCDText = GetBool("Audio", "AudioAutoCDText", settings.AudioAutoCDText);
        settings.AudioExplorerViewStyle = GetInt("Audio", "AudioExplorerViewStyle", settings.AudioExplorerViewStyle);
        settings.AudioReplayGain = (ReplayGainMode)GetInt("Audio", "AudioReplayGain", (int)settings.AudioReplayGain);
        settings.AudioTileVertical = GetBool("Audio", "AudioTileVertical", settings.AudioTileVertical);
        settings.AudioPauseLength = GetInt("Audio", "AudioPauseLength", settings.AudioPauseLength);
        settings.AudioExplorer = GetBool("Audio", "AudioExplorer", settings.AudioExplorer);
        settings.AudioCloseDisc = GetBool("Audio", "AudioCloseDisc", settings.AudioCloseDisc);
        settings.AudioBurnEject = GetBool("Audio", "AudioBurnEject", settings.AudioBurnEject);
        settings.AudioBurnGapless = GetBool("Audio", "AudioBurnGapless", settings.AudioBurnGapless);
        settings.AudioCrossfadingDuration = GetDouble("Audio", "AudioCrossfadingDuration", settings.AudioCrossfadingDuration);
        settings.AudioShowPlayer = GetBool("Audio", "AudioShowPlayer", settings.AudioShowPlayer);
        settings.AudioOpenPlaylistFolder = GetStr("Audio", "AudioOpenPlaylistFolder", settings.AudioOpenPlaylistFolder);
        settings.AudioSaveCompilationPath = GetStr("Audio", "AudioSaveCompilationPath", settings.AudioSaveCompilationPath);
        settings.AudioLastDeviceUsed = GetStr("Audio", "AudioLastDeviceUsed", settings.AudioLastDeviceUsed);
        settings.AudioFilePattern = GetStr("Audio", "AudioFilePattern", settings.AudioFilePattern);
        settings.AudioFilePatternInclude = GetBool("Audio", "AudioFilePatternInclude", settings.AudioFilePatternInclude);
        settings.AudioFilePatternShowInBrowser = GetBool("Audio", "AudioFilePatternShowInBrowser", settings.AudioFilePatternShowInBrowser);
        settings.LastAudioBurnSpeed = GetInt("Audio", "LastAudioBurnSpeed", settings.LastAudioBurnSpeed);
        settings.CDTextEnabled = GetBool("Audio", "CDTextEnabled", settings.CDTextEnabled);

        return settings;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var currentSection = string.Empty;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                if (!result.ContainsKey(currentSection))
                {
                    result[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (!result.TryGetValue(currentSection, out var dict))
                {
                    dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[currentSection] = dict;
                }
                dict[key] = value;
            }
        }

        return result;
    }
}
