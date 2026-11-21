using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CDBurner10.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        App.LaunchArgs = args;
        Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
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
        Services.AppServices.Current.Dialogs = new Views.Dialogs.DialogService();
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
