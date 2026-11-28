using CDBurner10.Upscaler.Engine;
using CDBurner10.Upscaler.Services;
using CDBurner10.Upscaler.Views;
using CDBurner10.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace CDBurner10.Upscaler;

public sealed partial class MainWindow : Window
{
    private TrayIconService? _trayService;
    private AppWindow? _appWindow;
    private bool _allowClose;

    private static readonly Dictionary<string, Type> PageRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["upscale"] = typeof(UpscalePage),
        ["settings"] = typeof(SettingsPage)
    };

    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindowChrome();
        ApplySavedTheme();

        UpscalerAppServices.Current.NavigationHandler = NavigateTo;
        UpscalerAppServices.Current.PropertyChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(UpdateStatusBar);

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;

        _ = InitializeAsync();
    }

    private void ConfigureWindowChrome()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new SizeInt32(1180, 820));
        _appWindow.Closing += MainWindow_AppWindowClosing;

        FilePickerHelper.RegisterWindowHandleProvider(() =>
            App.MainWindow is null ? nint.Zero : WindowNative.GetWindowHandle(App.MainWindow));

        _trayService = new TrayIconService(hwnd, "CDBurner10 Upscaler");
        _trayService.RestoreRequested += (_, _) => DispatcherQueue.TryEnqueue(() => _trayService.RestoreFromTray());
        _trayService.ExitRequested += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _allowClose = true;
            _trayService.RemoveTrayIcon();
            Close();
        });

        UpscalerAppServices.Current.Operations.AttachWindow(hwnd, DispatcherQueue, _trayService);
    }

    private void ApplySavedTheme()
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = UpscalerAppServices.Current.Settings.Current.Theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    private async Task InitializeAsync()
    {
        await Task.Run(() => UpscalerAppServices.Current.RefreshEngineStatus());
        UpdateStatusBar();

        string? initialFile = null;
        if (App.LaunchArgs.Length > 0 && File.Exists(App.LaunchArgs[0]))
            initialFile = App.LaunchArgs[0];

        NavigateTo("upscale", initialFile);
    }

    public void NavigateTo(string tag, object? parameter = null)
    {
        if (!PageRoutes.TryGetValue(tag, out var pageType))
            return;

        foreach (var item in NavView.MenuItems.Concat(NavView.FooterMenuItems))
        {
            if (item is NavigationViewItem nvi &&
                nvi.Tag is string t &&
                t.Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                if (!ReferenceEquals(NavView.SelectedItem, nvi))
                    NavView.SelectedItem = nvi;
                break;
            }
        }

        if (ContentFrame.CurrentSourcePageType != pageType || parameter is not null)
            ContentFrame.Navigate(pageType, parameter);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item &&
            item.Tag is string tag &&
            PageRoutes.TryGetValue(tag, out var pageType) &&
            ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void UpdateStatusBar()
    {
        var svc = UpscalerAppServices.Current;
        LibMpvBadgeText.Text = svc.IsLibMpvAvailable ? "libmpv ready" : "libmpv missing";
        ToolTipService.SetToolTip(LibMpvBadgeText, svc.LibMpvStatusText);
        EngineStatusText.Text = svc.EngineStatusSummary;
    }

    private async void RefreshEnginesButton_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => UpscalerAppServices.Current.RefreshEngineStatus());
        UpdateStatusBar();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
            UpscalerAppServices.Current.Operations.OnWindowActivated();
    }

    private void MainWindow_AppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose && UpscalerAppServices.Current.Settings.Current.ExitToNotify)
        {
            args.Cancel = true;
            _trayService?.HideToTray("CDBurner10 Upscaler");
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        UpscalerAppServices.Current.Operations.DetachWindow();
        _trayService?.Dispose();
        UpscalerAppServices.Current.Dispose();
    }
}