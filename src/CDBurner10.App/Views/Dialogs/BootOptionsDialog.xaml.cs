using System.Globalization;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Iso;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed partial class BootOptionsDialog : ContentDialog
{
    private readonly GenericDataBurner _burner;

    public BootOptionsDialog(GenericDataBurner burner)
    {
        InitializeComponent();
        _burner = burner;

        var boot = burner.BootImage;
        BootableToggle.IsOn = boot.Enabled;
        BootImagePathBox.Text = boot.FileName;
        LoadSegmentBox.Text = boot.LoadSegment.ToString("X", CultureInfo.InvariantCulture);
        SectorsBox.Value = Math.Clamp(boot.SectorsToLoad, 1, 255);
        PlatformComboBox.SelectedIndex = Math.Clamp(boot.Platform, 0, 2);
        EmulationComboBox.SelectedIndex = boot.EmulationType switch
        {
            BootEmulationType.Floppy144 => 1,
            BootEmulationType.Floppy288 => 2,
            BootEmulationType.HardDisk => 3,
            _ => 0
        };

        UpdateEnabledState();
    }

    private void BootableToggle_Toggled(object sender, RoutedEventArgs e) => UpdateEnabledState();

    private void UpdateEnabledState()
    {
        BootSettingsPanel.Visibility = BootableToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BrowseBootImageButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".ima", ".img", ".bin", ".bif"]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            BootImagePathBox.Text = path;
        }
    }

    private async void CreateFloppyImageButton_Click(object sender, RoutedEventArgs e)
    {
        var letter = FloppyDriveLetterBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(letter))
        {
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "Drive Letter Required";
            StatusInfoBar.Message = "Enter a floppy or removable drive letter (e.g. A:).";
            StatusInfoBar.IsOpen = true;
            return;
        }

        var outputPath = await FilePickerHelper.PickSaveFileAsync("bootfloppy", new Dictionary<string, IList<string>>
        {
            ["Boot Image"] = [".ima", ".img"]
        });
        if (string.IsNullOrWhiteSpace(outputPath)) return;

        CreateFloppyImageButton.IsEnabled = false;
        FloppyProgressBar.Visibility = Visibility.Visible;
        FloppyProgressBar.Value = 0;

        using var op = AppServices.Current.Operations.Begin(
            Loc.T("Create Boot Image"),
            $"Reading boot image from {letter}...");

        try
        {
            var progress = new Progress<double>(p =>
            {
                double clamped = Math.Clamp(p, 0, 100);
                FloppyProgressBar.Value = clamped;
                op.Report(clamped, BurnPhase.Grabbing);
            });
            await IsoTools.ReadImageFromFloppyDeviceAsync(letter, outputPath, progress);
            BootImagePathBox.Text = outputPath;
            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "Floppy Image Created";
            StatusInfoBar.Message = $"Saved boot image to '{Path.GetFileName(outputPath)}'.";
            StatusInfoBar.IsOpen = true;
            op.Complete($"Saved boot image to '{Path.GetFileName(outputPath)}'.");
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
        }
        catch (Exception ex)
        {
            op.Fail(ex.Message);
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Floppy Read Failed";
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
        }
        finally
        {
            CreateFloppyImageButton.IsEnabled = true;
            FloppyProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (BootableToggle.IsOn)
        {
            var path = BootImagePathBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                args.Cancel = true;
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "Boot Image Required";
                StatusInfoBar.Message = "Select an existing boot image file or disable bootable disc.";
                StatusInfoBar.IsOpen = true;
                return;
            }
        }

        if (!int.TryParse(LoadSegmentBox.Text?.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int loadSeg))
        {
            loadSeg = 0x7C0;
        }

        _burner.BootImage = new BootImageOptions
        {
            Enabled = BootableToggle.IsOn,
            FileName = BootImagePathBox.Text?.Trim() ?? string.Empty,
            Platform = Math.Clamp(PlatformComboBox.SelectedIndex, 0, 2),
            LoadSegment = loadSeg,
            SectorsToLoad = Math.Max(1, (int)SectorsBox.Value),
            EmulationType = EmulationComboBox.SelectedIndex switch
            {
                1 => BootEmulationType.Floppy144,
                2 => BootEmulationType.Floppy288,
                3 => BootEmulationType.HardDisk,
                _ => BootEmulationType.NoEmulation
            }
        };
    }
}
