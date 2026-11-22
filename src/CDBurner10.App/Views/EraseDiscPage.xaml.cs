using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views;

public sealed partial class EraseDiscPage : Page
{
    private readonly DiscEraserService _eraser = new();
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private DiscInfoModel _currentDisc = DiscInfoModel.Empty;

    public EraseDiscPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += EraseDiscPage_Loaded;
        Unloaded += EraseDiscPage_Unloaded;
    }

    private void EraseDiscPage_Loaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed += OnDrivesRefreshed;
        if (_isBusy) return;
        PopulateDrives();
    }

    private void EraseDiscPage_Unloaded(object sender, RoutedEventArgs e)
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
                "Connect an optical drive with a rewritable disc (CD-RW, DVD±RW, BD-RE) to erase.");
        }

        _ = RefreshDiscStatusAsync();
    }

    private async Task RefreshDiscStatusAsync()
    {
        var drive = DriveComboBox.SelectedItem as DriveModel;
        if (drive is null)
        {
            _currentDisc = DiscInfoModel.Empty;
            MediaTypeText.Text = "No Drive";
            DiscStateText.Text = "—";
            ErasableText.Text = "No";
            EraseButton.IsEnabled = false;
            return;
        }

        MediaTypeText.Text = "Checking...";
        _currentDisc = await drive.RefreshDiscInfoAsync();

        if (!_currentDisc.HasDisc)
        {
            MediaTypeText.Text = "No Disc Inserted";
            DiscStateText.Text = "Empty Tray";
            ErasableText.Text = "No";
            EraseButton.IsEnabled = false;
            ShowStatus(InfoBarSeverity.Informational, "Insert Rewritable Disc",
                $"Insert a CD-RW, DVD±RW, or BD-RE disc into {drive.DisplayName} and click Refresh.");
        }
        else
        {
            MediaTypeText.Text = _currentDisc.DiscTypeDescription;
            DiscStateText.Text = _currentDisc.IsBlank ? "Blank" : (_currentDisc.IsNextSessionAllowed ? "Open" : "Finalized");
            ErasableText.Text = _currentDisc.IsErasable ? "Yes (Rewritable)" : "No (Write-Once / ROM)";
            EraseButton.IsEnabled = !_isBusy && _currentDisc.IsErasable;

            if (!_currentDisc.IsErasable)
            {
                ShowStatus(InfoBarSeverity.Warning, "Disc Not Erasable",
                    $"The inserted {_currentDisc.DiscTypeDescription} disc cannot be erased.");
            }
            else
            {
                StatusInfoBar.IsOpen = false;
            }
        }
    }

    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = RefreshDiscStatusAsync();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshDiscStatusAsync();
    }

    private async void EjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DriveComboBox.SelectedItem is DriveModel drive)
        {
            await drive.EjectAsync();
            await RefreshDiscStatusAsync();
        }
    }

    private async void EraseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var drive = DriveComboBox.SelectedItem as DriveModel;
        if (drive is null) return;

        bool fullErase = EraseMethodRadio.SelectedIndex == 1;
        bool confirmed = await AppServices.Current.Dialogs.ConfirmAsync(
            XamlRoot,
            "Confirm Erase Disc",
            $"All existing data on the {_currentDisc.DiscTypeDescription} in {drive.DisplayName} will be permanently erased.\n\nMethod: {(fullErase ? "Secure / Full Erase" : "Fast Erase")}",
            "Erase",
            "Cancel");
        if (!confirmed) return;

        _isBusy = true;
        EraseButton.IsEnabled = false;
        DriveComboBox.IsEnabled = false;
        EraseMethodRadio.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ProgressCard.Visibility = Visibility.Visible;
        EraseProgressBar.Value = 0;
        ProgressStatusText.Text = "Starting erase...";
        ProgressTimeText.Text = string.Empty;

        _cts = new CancellationTokenSource();
        var progress = new Progress<EraseProgress>(p =>
        {
            EraseProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            ProgressStatusText.Text = string.IsNullOrWhiteSpace(p.Message) ? $"Erasing... {p.Percent}%" : p.Message;
            ProgressTimeText.Text = p.TimeRemaining > TimeSpan.Zero
                ? $"Remaining: {FormatHelper.FormatDuration(p.TimeRemaining)}"
                : $"{p.Percent}%";
        });

        try
        {
            await _eraser.EraseAsync(drive, fullErase, EjectAfterEraseCheckBox.IsChecked == true, progress, _cts.Token);
            ShowStatus(InfoBarSeverity.Success, "Disc Erased", "The disc has been erased successfully.");
        }
        catch (OperationCanceledException)
        {
            ShowStatus(InfoBarSeverity.Warning, "Cancelled", "Erase operation was cancelled.");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Erase Failed", ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _isBusy = false;
            DriveComboBox.IsEnabled = true;
            EraseMethodRadio.IsEnabled = true;
            CancelButton.IsEnabled = false;
            await RefreshDiscStatusAsync();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
