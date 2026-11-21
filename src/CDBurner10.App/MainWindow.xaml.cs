using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.App.Views;
using CDBurner10.Core.Burning;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace CDBurner10.App;

public sealed partial class MainWindow : Window
{
    private DeviceChangeMonitor? _deviceMonitor;
    private DropBoxWindow? _dropBoxWindow;
    private bool _suppressDriveSelectionChanged;

    private static readonly Dictionary<string, Type> PageRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = typeof(HomePage),
        ["data"] = typeof(DataCompilationPage),
        ["audio"] = typeof(AudioCompilationPage),
        ["video"] = typeof(VideoDvdPage),
        ["iso"] = typeof(BurnIsoPage),
        ["copy"] = typeof(CopyDiscPage),
        ["erase"] = typeof(EraseDiscPage),
        ["convert"] = typeof(ConvertIsoPage),
        ["info"] = typeof(DriveDiscInfoPage),
        ["cover"] = typeof(CoverPrintPage),
        ["settings"] = typeof(SettingsPage)
    };

    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindowChrome();

        AppServices.Current.NavigationHandler = NavigateTo;
        AppServices.Current.DrivesRefreshed += (_, _) => DispatcherQueue.TryEnqueue(UpdateDriveBar);
        AppServices.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppServices.SelectedDrive) or nameof(AppServices.SelectedDriveDiscInfo) or nameof(AppServices.EngineStatusMessage))
                DispatcherQueue.TryEnqueue(UpdateDriveBar);
        };

        HookDeviceChangeMonitor();
        Closed += MainWindow_Closed;

        _ = InitializeAndNavigateAsync();
    }

    private void ConfigureWindowChrome()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(1280, 820));
    }

    private void HookDeviceChangeMonitor()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        _deviceMonitor = new DeviceChangeMonitor(hwnd);
        _deviceMonitor.MediaChanged += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(async () => await AppServices.Current.RefreshDrivesAsync());
        };
    }

    private async Task InitializeAndNavigateAsync()
    {
        await Task.Run(() => AppServices.Current.InitializeEngines());
        await AppServices.Current.RefreshDrivesAsync();
        UpdateDriveBar();
        HandleLaunchArguments(App.LaunchArgs);
    }

    private void HandleLaunchArguments(string[] args)
    {
        if (args.Length > 0)
        {
            string first = args[0].Trim();
            string? second = args.Length > 1 ? args[1].Trim() : null;

            switch (first.ToLowerInvariant())
            {
                case "/od":
                case "/od+update":
                    NavigateTo("data", second);
                    return;
                case "/oa":
                    NavigateTo("audio", second);
                    return;
                case "/oi":
                    NavigateTo("iso", second);
                    return;
                case "/ov":
                    NavigateTo("video", second);
                    return;
                case "/oc":
                    NavigateTo("copy");
                    return;
                case "/oe":
                    NavigateTo("erase");
                    return;
            }

            if (File.Exists(first))
            {
                if (first.EndsWith(".dxp", StringComparison.OrdinalIgnoreCase))
                {
                    NavigateTo("data", first);
                    return;
                }
                if (first.EndsWith(".axp", StringComparison.OrdinalIgnoreCase))
                {
                    NavigateTo("audio", first);
                    return;
                }
                if (first.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ||
                    first.EndsWith(".mds", StringComparison.OrdinalIgnoreCase))
                {
                    NavigateTo("iso", first);
                    return;
                }
            }
        }

        var settings = AppServices.Current.Settings.Current;
        if (settings.StartLastUsedProjectType &&
            !string.IsNullOrWhiteSpace(settings.LastUsedProjectType) &&
            PageRoutes.ContainsKey(settings.LastUsedProjectType))
        {
            NavigateTo(settings.LastUsedProjectType);
        }
        else
        {
            NavigateTo("home");
        }
    }

    public void NavigateTo(string tag, object? parameter = null)
    {
        if (tag.Equals("dropbox", StringComparison.OrdinalIgnoreCase))
        {
            ToggleDropBox();
            return;
        }

        if (!PageRoutes.TryGetValue(tag, out var pageType))
            return;

        if (tag is "data" or "audio" or "video" or "iso" or "copy" or "erase")
        {
            AppServices.Current.Settings.Current.LastUsedProjectType = tag;
        }

        // Sync NavigationView selection
        var targetItem = FindNavItemByTag(tag);
        if (targetItem is not null && !ReferenceEquals(NavView.SelectedItem, targetItem))
            NavView.SelectedItem = targetItem;

        if (ContentFrame.CurrentSourcePageType != pageType || parameter is not null)
            ContentFrame.Navigate(pageType, parameter);

        UpdateDropBoxTarget();
    }

    private NavigationViewItem? FindNavItemByTag(string tag)
    {
        foreach (var item in NavView.MenuItems.Concat(NavView.FooterMenuItems))
        {
            if (item is NavigationViewItem nvi &&
                nvi.Tag is string t &&
                t.Equals(tag, StringComparison.OrdinalIgnoreCase))
                return nvi;
        }
        return null;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            if (PageRoutes.TryGetValue(tag, out var pageType) && ContentFrame.CurrentSourcePageType != pageType)
            {
                if (tag is "data" or "audio" or "video" or "iso" or "copy" or "erase")
                    AppServices.Current.Settings.Current.LastUsedProjectType = tag;

                ContentFrame.Navigate(pageType);
                UpdateDropBoxTarget();
            }
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item &&
            item.Tag is string tag &&
            tag.Equals("dropbox", StringComparison.OrdinalIgnoreCase))
        {
            ToggleDropBox();
        }
    }

    public void ToggleDropBox()
    {
        if (_dropBoxWindow is not null)
        {
            _dropBoxWindow.Close();
            _dropBoxWindow = null;
            return;
        }

        _dropBoxWindow = new DropBoxWindow();
        _dropBoxWindow.FilesDropped += (_, paths) =>
        {
            DispatcherQueue.TryEnqueue(() => AppServices.Current.RouteDroppedFiles(paths));
        };
        _dropBoxWindow.Closed += (_, _) => _dropBoxWindow = null;
        UpdateDropBoxTarget();
        _dropBoxWindow.Activate();
    }

    private void UpdateDropBoxTarget()
    {
        if (_dropBoxWindow is null) return;
        bool isAudio = AppServices.Current.ActiveCompilationMode.Equals("Audio", StringComparison.OrdinalIgnoreCase);
        _dropBoxWindow.UpdateTarget(
            isAudio ? "Audio Disc" : "Data Disc",
            isAudio
                ? $"{AppServices.Current.ActiveAudioCompilation.Tracks.Count} track(s) in compilation"
                : $"{AppServices.Current.ActiveDataCompilation.DataBurner.FileCount} file(s) in compilation");
    }

    private void UpdateDriveBar()
    {
        var services = AppServices.Current;
        _suppressDriveSelectionChanged = true;

        DriveSelectorComboBox.ItemsSource = null;
        DriveSelectorComboBox.ItemsSource = services.Drives;
        DriveSelectorComboBox.DisplayMemberPath = nameof(DriveModel.DisplayName);
        DriveSelectorComboBox.SelectedItem = services.SelectedDrive;

        bool hasDrive = services.SelectedDrive is not null;
        EjectTrayButton.IsEnabled = hasDrive;
        LoadTrayButton.IsEnabled = hasDrive;

        if (!hasDrive)
        {
            MediaStatusText.Text = "No optical drive detected";
        }
        else
        {
            var disc = services.SelectedDriveDiscInfo;
            if (!disc.HasDisc)
            {
                MediaStatusText.Text = "No disc inserted";
            }
            else if (disc.IsBlank)
            {
                MediaStatusText.Text = $"{disc.DiscTypeDescription} • {FormatHelper.FormatBytes(disc.FreeSizeBytes)} free";
            }
            else
            {
                MediaStatusText.Text = $"{disc.DiscTypeDescription} • {FormatHelper.FormatBytes(disc.UsedSizeBytes)} used / {FormatHelper.FormatBytes(disc.FreeSizeBytes)} free";
            }
        }

        EngineStatusText.Text = services.EngineStatusMessage;
        _suppressDriveSelectionChanged = false;
    }

    private async void DriveSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDriveSelectionChanged) return;
        if (DriveSelectorComboBox.SelectedItem is DriveModel drive)
        {
            AppServices.Current.SelectedDrive = drive;
            await AppServices.Current.RefreshSelectedDiscInfoAsync();
            UpdateDriveBar();
        }
    }

    private async void EjectTrayButton_Click(object sender, RoutedEventArgs e) =>
        await AppServices.Current.EjectSelectedDriveAsync();

    private async void LoadTrayButton_Click(object sender, RoutedEventArgs e) =>
        await AppServices.Current.LoadSelectedDriveAsync();

    private async void RefreshDrivesButton_Click(object sender, RoutedEventArgs e) =>
        await AppServices.Current.RefreshDrivesAsync();

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _dropBoxWindow?.Close();
        _deviceMonitor?.Dispose();
        AppServices.Current.Dispose();
    }
}
