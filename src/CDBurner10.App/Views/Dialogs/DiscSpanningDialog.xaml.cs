using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed record SplitDiscRow(int Index, string Label, string FileCountText, string SizeText);

public sealed partial class DiscSpanningDialog : ContentDialog
{
    private readonly DataCompilation _compilation;
    private readonly DriveModel? _preferredDrive;
    private readonly DiscSplitter _splitter = new();
    private IReadOnlyList<GenericDataBurner> _discs = [];

    public DiscSpanningDialog(DataCompilation compilation, DriveModel? preferredDrive)
    {
        InitializeComponent();
        _compilation = compilation;
        _preferredDrive = preferredDrive;
        SourceSummaryText.Text =
            $"Current compilation: {compilation.DataBurner.FileCount} file(s), {FormatHelper.FormatBytes(compilation.DataBurner.TotalSizeBytes)} total.";
    }

    private async void SplitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_compilation.DataBurner.FileCount == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "Empty Compilation", "Add files to your data compilation before splitting.");
            return;
        }

        long maxBytes = 4_700_000_000L;
        if (DiscSizeComboBox.SelectedItem is ComboBoxItem item &&
            long.TryParse(item.Tag?.ToString(), out long parsed))
        {
            maxBytes = parsed;
        }

        var algo = AlgorithmRadio.SelectedIndex == 1
            ? DiscSplitAlgorithm.OptimiseFolderStructure
            : DiscSplitAlgorithm.OptimiseDiscCount;

        SplitButton.IsEnabled = false;
        SplitProgressRing.IsActive = true;
        StatusInfoBar.IsOpen = false;

        try
        {
            var result = await _splitter.SplitAsync(
                _compilation,
                maxBytes,
                algo,
                IncludeTocCheckBox.IsChecked == true);

            if (result.FileTooBig is not null)
            {
                ShowStatus(InfoBarSeverity.Error, "File Too Large",
                    $"'{result.FileTooBig.Name}' ({FormatHelper.FormatBytes(result.FileTooBig.FileSizeBytes)}) exceeds the selected disc capacity.");
                ResultsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            _discs = result.Discs;
            var rows = _discs.Select((d, i) => new SplitDiscRow(
                i,
                d.VolumeName,
                $"{d.FileCount} file(s)",
                FormatHelper.FormatBytes(d.TotalSizeBytes))).ToList();

            SplitDiscsListView.ItemsSource = rows;
            if (rows.Count > 0) SplitDiscsListView.SelectedIndex = 0;

            ResultSummaryText.Text = $"Split into {_discs.Count} disc(s):";
            ResultsPanel.Visibility = Visibility.Visible;
            BurnSelectedButton.IsEnabled = _preferredDrive is not null || AppServices.Current.Drives.Count > 0;
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Split Failed", ex.Message);
        }
        finally
        {
            SplitButton.IsEnabled = true;
            SplitProgressRing.IsActive = false;
        }
    }

    private async void BurnSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (SplitDiscsListView.SelectedItem is not SplitDiscRow row || row.Index >= _discs.Count) return;
        var drive = _preferredDrive ?? AppServices.Current.SelectedDrive ?? AppServices.Current.Drives.FirstOrDefault();
        if (drive is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No Optical Drive", "Connect an optical writer or use 'Save All as ISOs...'.");
            return;
        }

        var disc = _discs[row.Index];
        BurnSelectedButton.IsEnabled = false;
        SaveIsosButton.IsEnabled = false;
        ActionProgressBar.Visibility = Visibility.Visible;
        ActionProgressBar.Value = 0;

        using var op = AppServices.Current.Operations.Begin(
            Loc.T("Disc Spanning — Burn"),
            $"Burning '{disc.VolumeName}' to {drive.DisplayName}...");

        try
        {
            var opts = new DataBurnOptions
            {
                Drive = drive,
                FinalizeDisc = true,
                EjectAfterBurn = true,
                VolumeName = disc.VolumeName
            };
            var prog = new Progress<BurnProgress>(p =>
            {
                ActionProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
                op.Report(p);
            });
            await disc.BurnAsync(opts, prog);
            ShowStatus(InfoBarSeverity.Success, "Disc Burned", $"Successfully burned '{disc.VolumeName}' to {drive.DisplayName}.");
            op.Complete($"Successfully burned '{disc.VolumeName}' to {drive.DisplayName}.");
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
        }
        catch (Exception ex)
        {
            op.Fail(ex.Message);
            ShowStatus(InfoBarSeverity.Error, "Burn Failed", ex.Message);
        }
        finally
        {
            BurnSelectedButton.IsEnabled = true;
            SaveIsosButton.IsEnabled = true;
            ActionProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void SaveIsosButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discs.Count == 0) return;
        var folder = await FilePickerHelper.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder)) return;

        BurnSelectedButton.IsEnabled = false;
        SaveIsosButton.IsEnabled = false;
        ActionProgressBar.Visibility = Visibility.Visible;

        using var op = AppServices.Current.Operations.Begin(
            Loc.T("Disc Spanning — Save ISOs"),
            $"Saving {_discs.Count} ISO image(s) to '{folder}'...");

        try
        {
            for (int i = 0; i < _discs.Count; i++)
            {
                var disc = _discs[i];
                var isoPath = Path.Combine(folder, $"{disc.VolumeName}_Disc{i + 1}.iso");
                int idx = i;
                var prog = new Progress<BurnProgress>(p =>
                {
                    double combined = Math.Clamp((idx * 100.0 + p.Percent) / _discs.Count, 0, 100);
                    ActionProgressBar.Value = combined;
                    op.Report(combined, BurnPhase.Converting, p.TimeRemaining);
                });
                await disc.SaveIsoAsync(isoPath, disc.VolumeName, prog);
            }
            ShowStatus(InfoBarSeverity.Success, "ISOs Saved", $"Saved {_discs.Count} ISO image(s) to '{folder}'.");
            op.Complete($"Saved {_discs.Count} ISO image(s).");
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
        }
        catch (Exception ex)
        {
            op.Fail(ex.Message);
            ShowStatus(InfoBarSeverity.Error, "Save Failed", ex.Message);
        }
        finally
        {
            BurnSelectedButton.IsEnabled = true;
            SaveIsosButton.IsEnabled = true;
            ActionProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
