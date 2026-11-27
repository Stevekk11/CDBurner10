using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Iso;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CDBurner10.App.Views;

public sealed partial class ConvertIsoPage : Page
{
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private string _lastOutputIso = string.Empty;

    public ConvertIsoPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_isBusy && e.Parameter is string src && !string.IsNullOrWhiteSpace(src))
        {
            SourcePathBox.Text = src;
        }
    }

    private async void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".bin", ".nrg", ".b5i"]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            SourcePathBox.Text = path;
        }
    }

    private async void BrowseDestButton_Click(object sender, RoutedEventArgs e)
    {
        var suggested = string.IsNullOrWhiteSpace(SourcePathBox.Text)
            ? "ConvertedImage"
            : Path.GetFileNameWithoutExtension(SourcePathBox.Text.Trim());

        var path = await FilePickerHelper.PickSaveFileAsync(suggested, new Dictionary<string, IList<string>>
        {
            ["ISO Disc Image"] = [".iso"]
        });
        if (!string.IsNullOrWhiteSpace(path))
        {
            DestinationPathBox.Text = path;
        }
    }

    private void SourcePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var src = SourcePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(src))
        {
            NrgAudioWarningBar.IsOpen = false;
            return;
        }

        // Auto-suggest destination .iso path
        try
        {
            var dir = Path.GetDirectoryName(src);
            var name = Path.GetFileNameWithoutExtension(src);
            if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(name))
            {
                DestinationPathBox.Text = Path.Combine(dir, name + ".iso");
            }
        }
        catch
        {
            // Ignore malformed path while typing
        }

        // Check NRG audio image warning
        bool isAudioNrg = false;
        if (src.EndsWith(".nrg", StringComparison.OrdinalIgnoreCase) && File.Exists(src))
        {
            try { isAudioNrg = IsoTools.IsNrgAudioImage(src); } catch { }
        }
        NrgAudioWarningBar.IsOpen = isAudioNrg;
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var src = SourcePathBox.Text?.Trim() ?? string.Empty;
        var dst = DestinationPathBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(src) || !File.Exists(src))
        {
            ShowStatus(InfoBarSeverity.Error, "Source File Missing", "Select an existing .bin, .nrg, or .b5i image file.");
            return;
        }

        if (string.IsNullOrWhiteSpace(dst))
        {
            ShowStatus(InfoBarSeverity.Warning, "Destination Missing", "Specify an output .iso file path.");
            return;
        }

        _isBusy = true;
        ConvertButton.IsEnabled = false;
        BrowseSourceButton.IsEnabled = false;
        BrowseDestButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        BurnResultButton.Visibility = Visibility.Collapsed;
        ProgressCard.Visibility = Visibility.Visible;
        ConvertProgressBar.Value = 0;
        ProgressStatusText.Text = $"Converting {Path.GetFileName(src)}...";
        ProgressPercentText.Text = "0%";

        _cts = new CancellationTokenSource();
        using var op = AppServices.Current.Operations.Begin(
            Loc.T("Convert to ISO"),
            $"Converting '{Path.GetFileName(src)}' to '{Path.GetFileName(dst)}'...");

        var progress = new Progress<double>(pct =>
        {
            double clamped = Math.Clamp(pct, 0, 100);
            ConvertProgressBar.Value = clamped;
            ProgressPercentText.Text = $"{clamped:0}%";
            op.Report(clamped, BurnPhase.Converting);
        });

        try
        {
            await IsoTools.ConvertToIsoAsync(src, dst, progress, _cts.Token);
            _lastOutputIso = dst;
            ProgressStatusText.Text = "Conversion complete.";
            BurnResultButton.Visibility = Visibility.Visible;
            ShowStatus(InfoBarSeverity.Success, "Converted Successfully", $"Created '{Path.GetFileName(dst)}'.");
            op.Complete($"Created '{Path.GetFileName(dst)}'.");
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
            ProgressStatusText.Text = "Conversion cancelled.";
            ShowStatus(InfoBarSeverity.Warning, "Cancelled", "Image conversion was cancelled.");
        }
        catch (Exception ex)
        {
            op.Fail(ex.Message);
            ProgressStatusText.Text = "Conversion failed.";
            ShowStatus(InfoBarSeverity.Error, "Conversion Failed", ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _isBusy = false;
            ConvertButton.IsEnabled = true;
            BrowseSourceButton.IsEnabled = true;
            BrowseDestButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void BurnResultButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastOutputIso))
            AppServices.Current.NavigateTo("iso", _lastOutputIso);
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
