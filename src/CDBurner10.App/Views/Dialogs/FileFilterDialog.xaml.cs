using CDBurner10.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed partial class FileFilterDialog : ContentDialog
{
    public bool WasCleared { get; private set; }

    public FileFilterDialog(string currentPattern, bool include, bool applyToBrowser)
    {
        InitializeComponent();
        PatternBox.Text = currentPattern ?? string.Empty;
        ModeRadio.SelectedIndex = include ? 0 : 1;
        ApplyToBrowserCheckBox.IsChecked = applyToBrowser;
    }

    public FileFilterResult GetResult() =>
        WasCleared
            ? new FileFilterResult(string.Empty, true, ApplyToBrowserCheckBox.IsChecked == true)
            : new FileFilterResult(
                PatternBox.Text?.Trim() ?? string.Empty,
                ModeRadio.SelectedIndex == 0,
                ApplyToBrowserCheckBox.IsChecked == true);

    private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        WasCleared = true;
        PatternBox.Text = string.Empty;
    }
}
