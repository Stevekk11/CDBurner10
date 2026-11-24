using System.Text;
using CDBurner10.Core.Settings;

namespace CDBurner10.Core.Utilities;

public enum DiagnosticLogLevel
{
    Low = 0,
    Medium = 1,
    High = 2
}

/// <summary>
/// Thread-safe diagnostic file logger controlled by AppSettings.LoggingEnabled and AppSettings.LoggingLevel.
/// Writes daily log files under SettingsDirectory\Logs\cdburner10-YYYY-MM-DD.log.
/// </summary>
public static class DiagnosticLogger
{
    private static readonly object WriteLock = new();
    private static bool? _overrideEnabled;
    private static int? _overrideLevel;
    private static string? _overrideDirectory;

    public static string LogDirectory =>
        !string.IsNullOrWhiteSpace(_overrideDirectory)
            ? _overrideDirectory
            : Path.Combine(SettingsService.Default.SettingsDirectory, "Logs");

    public static string CurrentLogFilePath =>
        Path.Combine(LogDirectory, $"cdburner10-{DateTime.Now:yyyy-MM-dd}.log");

    public static bool IsEnabled => _overrideEnabled ?? SettingsService.Default.Current.LoggingEnabled;

    public static DiagnosticLogLevel CurrentLevel =>
        MapSettingLevel(_overrideLevel ?? SettingsService.Default.Current.LoggingLevel);

    public static DiagnosticLogLevel MapSettingLevel(int settingValue) => settingValue switch
    {
        0 => DiagnosticLogLevel.Low,
        2 => DiagnosticLogLevel.High,
        _ => DiagnosticLogLevel.Medium
    };

    public static void Configure(bool? enabled = null, int? level = null, string? logDirectory = null)
    {
        _overrideEnabled = enabled;
        _overrideLevel = level;
        _overrideDirectory = logDirectory;
    }

    public static void ResetConfiguration()
    {
        _overrideEnabled = null;
        _overrideLevel = null;
        _overrideDirectory = null;
    }

    public static void Error(string category, string message) =>
        Write(DiagnosticLogLevel.Low, "ERR", category, message);

    public static void Warn(string category, string message) =>
        Write(DiagnosticLogLevel.Low, "WRN", category, message);

    public static void Info(string category, string message) =>
        Write(DiagnosticLogLevel.Medium, "INF", category, message);

    public static void Verbose(string category, string message) =>
        Write(DiagnosticLogLevel.High, "DBG", category, message);

    private static void Write(DiagnosticLogLevel requiredLevel, string tag, string category, string message)
    {
        if (!IsEnabled || CurrentLevel < requiredLevel || string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            string dir = LogDirectory;
            Directory.CreateDirectory(dir);
            string path = CurrentLogFilePath;
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] [{category}] {message}{Environment.NewLine}";
            lock (WriteLock)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Never let diagnostic logging disrupt optical operations.
        }
    }
}
