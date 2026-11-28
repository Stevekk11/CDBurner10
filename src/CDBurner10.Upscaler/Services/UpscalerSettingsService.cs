using System.Text.Json;
using CDBurner10.Core.Utilities;

namespace CDBurner10.Upscaler.Services;

public sealed class UpscalerSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly object DefaultLock = new();
    private static UpscalerSettingsService? _default;

    public static UpscalerSettingsService Default
    {
        get
        {
            if (_default is null)
            {
                lock (DefaultLock)
                {
                    if (_default is null)
                    {
                        var svc = new UpscalerSettingsService();
                        svc.Load();
                        _default = svc;
                    }
                }
            }

            return _default;
        }
    }

    public UpscalerSettings Current { get; private set; } = new();
    public bool IsPortableMode { get; }
    public string SettingsDirectory { get; }
    public string SettingsFilePath { get; }

    public UpscalerSettingsService(string? customSettingsDirectory = null)
    {
        if (!string.IsNullOrEmpty(customSettingsDirectory))
        {
            SettingsDirectory = customSettingsDirectory;
            IsPortableMode = false;
        }
        else
        {
            IsPortableMode = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));
            SettingsDirectory = IsPortableMode
                ? AppContext.BaseDirectory
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CDBurner10");
        }

        SettingsFilePath = Path.Combine(SettingsDirectory, "upscaler_settings.json");
    }

    public UpscalerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<UpscalerSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    Current = loaded;
                    ApplyLoggerSettings();
                    return Current;
                }
            }
        }
        catch
        {
            // Fall back to defaults on corrupted settings
        }

        Current = new UpscalerSettings();
        ApplyLoggerSettings();
        return Current;
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
        ApplyLoggerSettings();
    }

    public void ResetToDefaults()
    {
        Current = new UpscalerSettings();
        Save();
    }

    public void ApplyLoggerSettings()
    {
        DiagnosticLogger.Configure(
            enabled: Current.LoggingEnabled,
            level: Current.LoggingLevel,
            logDirectory: Path.Combine(SettingsDirectory, "Logs"));
    }
}