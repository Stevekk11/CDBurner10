using System.Diagnostics;
using System.Text;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CDBurner10.App.Views;

public sealed partial class BurnIsoPage : Page
{
    private readonly ImageBurner _burner = new();
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private string? _navigatedPath;

    public BurnIsoPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += BurnIsoPage_Loaded;
        Unloaded += BurnIsoPage_Unloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_isBusy && e.Parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            _navigatedPath = path;
            IsoPathBox.Text = path;
        }
    }

    private void BurnIsoPage_Loaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed += OnDrivesRefreshed;
        if (_isBusy) return;

        var s = AppServices.Current.Settings.Current;
        FinalizeCheckBox.IsChecked = s.IsoFinalize;
        EjectCheckBox.IsChecked = s.IsoEjectDisc;
        Mode2XaCheckBox.IsChecked = s.IsoMode2XA;
        if (string.IsNullOrWhiteSpace(_navigatedPath) &&
            string.IsNullOrWhiteSpace(IsoPathBox.Text) &&
            !string.IsNullOrWhiteSpace(s.LastUsedIsoPath) &&
            File.Exists(s.LastUsedIsoPath))
        {
            IsoPathBox.Text = s.LastUsedIsoPath;
        }

        PopulateDrives();
        UpdateImageInfo();
    }

    private void BurnIsoPage_Unloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed -= OnDrivesRefreshed;
    }

    private void OnDrivesRefreshed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(PopulateDrives);
    }

    private void PopulateDrives()
    {
        var drives = AppServices.Current.Drives;
        var prev = DriveComboBox.SelectedItem as DriveModel ?? AppServices.Current.SelectedDrive;
        DriveComboBox.ItemsSource = null;
        DriveComboBox.ItemsSource = drives;
        DriveComboBox.DisplayMemberPath = nameof(DriveModel.DisplayName);
        DriveComboBox.SelectedItem = drives.FirstOrDefault(d => d.Letter == prev?.Letter) ?? drives.FirstOrDefault();

        if (drives.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "No Optical Drive Detected",
                "Connect a CD/DVD/Blu-ray burner to burn an ISO image.");
        }
        else if (StatusInfoBar.Title == "No Optical Drive Detected")
        {
            StatusInfoBar.IsOpen = false;
        }

        PopulateSpeeds();
    }

    private void PopulateSpeeds()
    {
        var drive = DriveComboBox.SelectedItem as DriveModel;
        SpeedComboBox.ItemsSource = drive?.SupportedWriteSpeeds ?? [new DriveSpeedModel(0, 0)];
        SpeedComboBox.DisplayMemberPath = nameof(DriveSpeedModel.DisplayText);
        SpeedComboBox.SelectedIndex = 0;
    }

    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => PopulateSpeeds();

    private async void BrowseIsoButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".iso", ".mds", ".bin", ".nrg", ".b5i"]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            IsoPathBox.Text = path;
        }
    }

    private void IsoPathBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateImageInfo();

    private void UpdateImageInfo()
    {
        var path = IsoPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ImageSizeText.Visibility = Visibility.Collapsed;
            ConvertHintBar.IsOpen = false;
            return;
        }

        var fi = new FileInfo(path);
        ImageSizeText.Text = $"Image size: {FormatHelper.FormatBytes(fi.Length)} ({fi.Length:N0} bytes)";
        ImageSizeText.Visibility = Visibility.Visible;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        ConvertHintBar.IsOpen = ext is ".bin" or ".nrg" or ".b5i";
    }

    private void ConvertToIsoLink_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Current.NavigateTo("convert", IsoPathBox.Text?.Trim());
    }

    private WriteMode GetSelectedWriteMode()
    {
        var tag = (WriteModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            "TAO" => WriteMode.TAO,
            "SAO" => WriteMode.SAO,
            "DAO96" => WriteMode.DAO96,
            _ => WriteMode.Auto
        };
    }

    private async void BurnButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var path = IsoPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ShowStatus(InfoBarSeverity.Error, "Image File Missing", "Please select an existing .iso or .mds image file.");
            return;
        }

        var drive = DriveComboBox.SelectedItem as DriveModel;
        if (drive is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No Drive Selected", "Connect and select an optical writer to burn this image.");
            return;
        }

        var s = AppServices.Current.Settings.Current;
        s.LastUsedIsoPath = path;
        s.IsoFinalize = FinalizeCheckBox.IsChecked == true;
        s.IsoEjectDisc = EjectCheckBox.IsChecked == true;
        s.IsoMode2XA = Mode2XaCheckBox.IsChecked == true;
        AppServices.Current.Settings.Save();

        var options = new ImageBurnOptions
        {
            Drive = drive,
            ImageFileName = path,
            WriteSpeedKBps = (SpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0,
            Mode = GetSelectedWriteMode(),
            IsMdsImage = path.EndsWith(".mds", StringComparison.OrdinalIgnoreCase),
            IsXaCdRom = Mode2XaCheckBox.IsChecked == true,
            Padding = PaddingCheckBox.IsChecked == true,
            FinalizeDisc = FinalizeCheckBox.IsChecked == true,
            VerifyData = VerifyCheckBox.IsChecked == true,
            TestBurn = SimulateCheckBox.IsChecked == true,
            EjectAfterBurn = EjectCheckBox.IsChecked == true,
            Copies = Math.Max(1, (int)CopiesBox.Value)
        };

        _isBusy = true;
        SetBusyUi(true);
        ProgressCard.Visibility = Visibility.Visible;
        BurnProgressBar.Value = 0;
        var log = new StringBuilder();
        void AppendLog(string msg)
        {
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            EventLogBox.Text = log.ToString();
        }

        AppendLog($"Starting burn of '{Path.GetFileName(path)}' to {drive.DisplayName}...");
        var sw = Stopwatch.StartNew();
        _cts = new CancellationTokenSource();
        string lastLogged = string.Empty;

        var progress = new Progress<BurnProgress>(p =>
        {
            BurnProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            ProgressPhaseText.Text = string.IsNullOrWhiteSpace(p.Message) ? $"{p.Phase} ({p.Percent}%)" : p.Message;
            var rem = p.TimeRemaining > TimeSpan.Zero ? $" • Remaining: {FormatHelper.FormatDuration(p.TimeRemaining)}" : string.Empty;
            ProgressTimeText.Text = $"Elapsed: {FormatHelper.FormatDuration(sw.Elapsed)}{rem}";
            if (!string.IsNullOrWhiteSpace(p.Message) && p.Message != lastLogged && p.Phase != BurnPhase.Burning)
            {
                lastLogged = p.Message;
                AppendLog(p.Message);
            }
        });

        try
        {
            await _burner.BurnAsync(options, progress, _cts.Token);
            AppendLog("ISO image burned successfully.");
            ShowStatus(InfoBarSeverity.Success, "Burn Completed", $"Successfully burned '{Path.GetFileName(path)}'.");

            if (ShutdownCheckBox.IsChecked == true && !_cts.IsCancellationRequested)
            {
                AppendLog("Initiating system shutdown...");
                SystemPowerHelper.ShutdownComputer();
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Burn cancelled by user.");
            ShowStatus(InfoBarSeverity.Warning, "Cancelled", "Image burn was cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            ShowStatus(InfoBarSeverity.Error, "Burn Failed", ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _isBusy = false;
            SetBusyUi(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _burner.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void SetBusyUi(bool busy)
    {
        BurnButton.IsEnabled = !busy;
        BrowseIsoButton.IsEnabled = !busy;
        DriveComboBox.IsEnabled = !busy;
        SpeedComboBox.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
