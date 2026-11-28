using CDBurner10.Upscaler.Engine;
using CDBurner10.Upscaler.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CDBurner10.Upscaler;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0].Equals("--cli-upscale", StringComparison.OrdinalIgnoreCase))
        {
            return RunCliUpscaleAsync(args).GetAwaiter().GetResult();
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        App.LaunchArgs = args;
        Application.Start(_p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static async Task<int> RunCliUpscaleAsync(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine(
                "Usage: CDBurner10.Upscaler --cli-upscale <input> <presetId> <outputFolder> [mp4|mkv]");
            return 2;
        }

        string input = args[1];
        string presetId = args[2];
        string outDir = args[3];
        string format = args.Length >= 5 ? args[4] : "mp4";

        var preset = UpscalerPresets.FindById(presetId);
        var settings = UpscalerSettingsService.Default.Current;
        var info = await VideoProbeService.ProbeAsync(input);
        var progress = new Progress<UpscaleProgress>(p =>
        {
            if (!string.IsNullOrWhiteSpace(p.LogLine))
                Console.WriteLine(p.LogLine);
        });

        string output = await UpscalePipeline.UpscaleAsync(info, preset, outDir, format, settings, progress,
            CancellationToken.None);
        Console.WriteLine($"OUTPUT={output}");
        return 0;
    }
}

public partial class App : Application
{
    public static string[] LaunchArgs { get; set; } = [];
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}