using CDBurner10.Core.Dvd;
using CDBurner10.Upscaler.Engine;
using CDBurner10.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDBurner10.Upscaler.Services;

public sealed partial class UpscalerAppServices : ObservableObject, IDisposable
{
    public static UpscalerAppServices Current { get; } = new();

    public UpscalerSettingsService Settings => UpscalerSettingsService.Default;

    public OperationTracker Operations { get; } = new(new OperationTrackerOptions(
        "CDBurner10 Upscaler",
        "cdburner10-upscaler-op",
        () => UpscalerSettingsService.Default.Current.ShowNotifications,
        () => UpscalerSettingsService.Default.Current.ShowProgressNotifications,
        ConvertingPhaseLabelOverride: "Upscaling"));

    [ObservableProperty] private bool _isLibMpvAvailable;

    [ObservableProperty] private bool _isFfmpegAvailable;

    [ObservableProperty] private string _engineStatusSummary = "Checking engines...";

    [ObservableProperty] private string _libMpvStatusText = "Checking libmpv-2.dll...";

    public Action<string, object?>? NavigationHandler { get; set; }

    private UpscalerAppServices()
    {
        Settings.ApplyLoggerSettings();
    }

    public void RefreshEngineStatus()
    {
        LibMpvNative.ResetLoadedLibrary();
        bool mpvOk = LibMpvNative.IsAvailable(out string? mpvPath, out string? mpvVer);
        IsLibMpvAvailable = mpvOk;
        LibMpvStatusText = mpvOk
            ? $"{mpvVer ?? "libmpv"} ready ({mpvPath})"
            : "libmpv-2.dll not found";

        if (!DvdVideoConverter.IsAvailable)
            DvdVideoConverter.ResetToolCache();

        bool ffOk = DvdVideoConverter.IsAvailable;
        IsFfmpegAvailable = ffOk;

        string encInfo;
        if (ffOk)
        {
            UpscalePipeline.ResolveVideoEncoder(Settings.Current, out string encLabel);
            encInfo = $"ffmpeg ready • {encLabel}";
        }
        else
        {
            encInfo = "ffmpeg not found";
        }

        EngineStatusSummary = $"{(mpvOk ? mpvVer ?? "libmpv ready" : "libmpv missing")} • {encInfo}";
    }

    public void NavigateTo(string tag, object? parameter = null)
    {
        NavigationHandler?.Invoke(tag, parameter);
    }

    public void Dispose()
    {
        Operations.Dispose();
        try
        {
            Settings.Save();
        }
        catch
        {
        }
    }
}