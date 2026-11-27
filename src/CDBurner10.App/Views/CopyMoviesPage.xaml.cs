using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CDBurner10.App.Views;

public sealed class MovieListRow : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public MovieListRow(DiscMovieItem item)
    {
        Item = item;
        string folder = string.IsNullOrEmpty(item.Subfolder) ? "(root)" : item.Subfolder;
        string subBadge = item.SidecarSubtitles.Count > 0
            ? $" • +{item.SidecarSubtitles.Count} subtitle(s)"
            : string.Empty;
        LocationSummary = $"{folder}{subBadge}";
        SizeText = FormatHelper.FormatBytes(item.SizeBytes);
    }

    public DiscMovieItem Item { get; }
    public string FileName => Item.FileName;
    public string LocationSummary { get; }
    public string SizeText { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class CopyMoviesPage : Page
{
    private readonly ObservableCollection<MovieListRow> _rows = [];
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _copyCts;
    private bool _isBusy;
    private string? _lastDestinationFolder;

    public CopyMoviesPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        MoviesListView.ItemsSource = _rows;
        Loaded += CopyMoviesPage_Loaded;
        Unloaded += CopyMoviesPage_Unloaded;
    }

    private void CopyMoviesPage_Loaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed += OnDrivesRefreshed;
        if (string.IsNullOrWhiteSpace(DestinationFolderBox.Text))
        {
            string defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(defaultDir))
                defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            DestinationFolderBox.Text = defaultDir;
        }

        if (!_isBusy)
            PopulateDrives();
    }

    private void CopyMoviesPage_Unloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed -= OnDrivesRefreshed;
    }

    private void OnDrivesRefreshed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isBusy) PopulateDrives();
        });
    }

    private void PopulateDrives()
    {
        var drives = AppServices.Current.Drives;
        var prev = SourceDriveComboBox.SelectedItem as DriveModel ?? AppServices.Current.SelectedDrive;

        SourceDriveComboBox.ItemsSource = null;
        SourceDriveComboBox.ItemsSource = drives;
        SourceDriveComboBox.DisplayMemberPath = nameof(DriveModel.DisplayName);
        SourceDriveComboBox.SelectedItem = drives.FirstOrDefault(d => d.Letter == prev?.Letter) ?? drives.FirstOrDefault();

        if (drives.Count == 0)
        {
            _rows.Clear();
            ScanStatusText.Text = "No optical drive detected.";
            UpdateSelectionSummary();
        }
    }

    private async void SourceDriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isBusy)
            await ScanSelectedDriveAsync();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        await ScanSelectedDriveAsync();
    }

    private async Task ScanSelectedDriveAsync()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        _rows.Clear();
        UpdateSelectionSummary();

        var drive = SourceDriveComboBox.SelectedItem as DriveModel;
        if (drive is null || string.IsNullOrWhiteSpace(drive.Letter))
        {
            ScanStatusText.Text = "Select an optical drive with a data disc to scan for movies.";
            return;
        }

        string rootPath = $"{drive.Letter}:\\";
        if (!Directory.Exists(rootPath))
        {
            ScanStatusText.Text = $"No readable data disc found in {drive.Letter}:.";
            return;
        }

        ScanButton.IsEnabled = false;
        ScanStatusText.Text = $"Scanning {rootPath} and all subfolders for movie files...";

        try
        {
            var found = await Task.Run(() => DiscMovieCopier.ScanDirectory(rootPath, ct), ct);
            if (ct.IsCancellationRequested) return;

            foreach (var item in found)
                _rows.Add(new MovieListRow(item));

            if (found.Count == 0)
            {
                ScanStatusText.Text = $"No movie files were found on {rootPath}.";
            }
            else
            {
                int folderCount = found.Select(f => f.Subfolder).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                long totalBytes = found.Sum(f => f.SizeBytes);
                ScanStatusText.Text =
                    $"Found {found.Count} movie(s) across {folderCount} folder(s) • {FormatHelper.FormatBytes(totalBytes)} total. Review the list below and click Start Copy.";
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scan
        }
        catch (Exception ex)
        {
            ScanStatusText.Text = $"Failed to scan {rootPath}: {ex.Message}";
        }
        finally
        {
            ScanButton.IsEnabled = !_isBusy;
            UpdateSelectionSummary();
        }
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = true;
        UpdateSelectionSummary();
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = false;
        UpdateSelectionSummary();
    }

    private void MovieRowCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateSelectionSummary();

    private void DestinationFolderBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSelectionSummary();

    private void UpdateSelectionSummary()
    {
        bool hasRows = _rows.Count > 0;
        SelectAllButton.IsEnabled = hasRows && !_isBusy;
        SelectNoneButton.IsEnabled = hasRows && !_isBusy;

        int selectedCount = _rows.Count(r => r.IsSelected);
        long selectedBytes = _rows.Where(r => r.IsSelected).Sum(r => r.Item.SizeBytes);

        SelectionSummaryText.Text = hasRows
            ? $"{selectedCount} of {_rows.Count} movie(s) selected • {FormatHelper.FormatBytes(selectedBytes)}"
            : "No movies found yet.";

        StartCopyButton.IsEnabled = !_isBusy &&
                                    selectedCount > 0 &&
                                    !string.IsNullOrWhiteSpace(DestinationFolderBox.Text);
    }

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        if (App.MainWindow is not null)
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            DestinationFolderBox.Text = folder.Path;
    }

    private async void StartCopyButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedItems = _rows.Where(r => r.IsSelected).Select(r => r.Item).ToList();
        string destFolder = DestinationFolderBox.Text.Trim();
        if (selectedItems.Count == 0 || string.IsNullOrWhiteSpace(destFolder))
            return;

        _isBusy = true;
        _copyCts = new CancellationTokenSource();
        SetControlsEnabled(false);
        UpdateSelectionSummary();
        OpenFolderButton.Visibility = Visibility.Collapsed;
        StatusInfoBar.IsOpen = false;
        CopyProgressBar.Value = 0;
        CopyProgressBar.Visibility = Visibility.Visible;
        ProgressText.Text = $"Starting copy of {selectedItems.Count} movie(s)...";
        ProgressText.Visibility = Visibility.Visible;

        bool preserveSubfolders = PreserveSubfoldersCheckBox.IsChecked == true;
        bool includeSubtitles = IncludeSubtitlesCheckBox.IsChecked == true;
        bool overwriteExisting = OverwriteExistingCheckBox.IsChecked == true;
        bool convertLegacyToMp4 = ConvertLegacyToMp4CheckBox.IsChecked == true;

        var progress = new Progress<MovieCopyProgress>(p =>
        {
            if (_copyCts?.IsCancellationRequested == true) return;
            CopyProgressBar.Value = p.Percent;
            ProgressText.Text = p.Message;
        });

        try
        {
            var result = await DiscMovieCopier.CopyMoviesAsync(
                selectedItems,
                destFolder,
                preserveSubfolders,
                includeSubtitles,
                overwriteExisting,
                progress,
                _copyCts.Token,
                convertLegacyToMp4);

            _lastDestinationFolder = result.DestinationFolder;
            OpenFolderButton.Visibility = Visibility.Visible;

            string subNote = result.SubtitlesCopied > 0 ? $" and {result.SubtitlesCopied} subtitle file(s)" : string.Empty;
            string convNote = result.FilesConverted > 0 ? $" ({result.FilesConverted} converted to MP4)" : string.Empty;
            if (result.FailedFiles.Count == 0)
            {
                ShowStatus(
                    InfoBarSeverity.Success,
                    "Movies Copied",
                    $"Copied {result.FilesCopied} movie(s){subNote}{convNote} ({FormatHelper.FormatBytes(result.TotalBytesCopied)}) to {result.DestinationFolder}.");
            }
            else
            {
                ShowStatus(
                    InfoBarSeverity.Warning,
                    "Completed with Some Errors",
                    $"Copied {result.FilesCopied} movie(s){subNote}, but {result.FailedFiles.Count} file(s) failed: {string.Join("; ", result.FailedFiles.Take(3))}");
            }
        }
        catch (OperationCanceledException)
        {
            CopyProgressBar.Visibility = Visibility.Collapsed;
            ProgressText.Visibility = Visibility.Collapsed;
            ShowStatus(InfoBarSeverity.Informational, "Copy Cancelled", "Movie copy was cancelled.");
        }
        catch (Exception ex)
        {
            CopyProgressBar.Visibility = Visibility.Collapsed;
            ProgressText.Visibility = Visibility.Collapsed;
            ShowStatus(InfoBarSeverity.Error, "Copy Failed", ex.Message);
        }
        finally
        {
            _copyCts.Dispose();
            _copyCts = null;
            _isBusy = false;
            SetControlsEnabled(true);
            UpdateSelectionSummary();
        }
    }

    private void CancelCopyButton_Click(object sender, RoutedEventArgs e)
    {
        CancelCopyButton.IsEnabled = false;
        ProgressText.Text = "Cancelling...";
        _copyCts?.Cancel();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastDestinationFolder) && Directory.Exists(_lastDestinationFolder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", _lastDestinationFolder) { UseShellExecute = true });
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        SourceDriveComboBox.IsEnabled = enabled;
        ScanButton.IsEnabled = enabled;
        DestinationFolderBox.IsEnabled = enabled;
        BrowseFolderButton.IsEnabled = enabled;
        PreserveSubfoldersCheckBox.IsEnabled = enabled;
        IncludeSubtitlesCheckBox.IsEnabled = enabled;
        OverwriteExistingCheckBox.IsEnabled = enabled;
        ConvertLegacyToMp4CheckBox.IsEnabled = enabled;
        CancelCopyButton.IsEnabled = !enabled;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
