using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed partial class FileSystemDialog : ContentDialog
{
    private readonly GenericDataBurner _burner;

    public FileSystemDialog(GenericDataBurner burner)
    {
        InitializeComponent();
        _burner = burner;

        FileSystemRadio.SelectedIndex = burner.FileSystemType == DataFileSystem.Udf ? 1 : 0;
        UdfVersionComboBox.SelectedIndex = (int)burner.UdfVersion;
        UpdateInfoTable();
    }

    private void FileSystemRadio_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInfoTable();
    private void UdfVersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInfoTable();

    private void UpdateInfoTable()
    {
        if (UdfVersionComboBox is null) return;
        bool isUdf = FileSystemRadio.SelectedIndex == 1;
        UdfVersionComboBox.IsEnabled = isUdf;

        if (isUdf)
        {
            MaxFileSizeText.Text = "Unlimited (> 4 GiB supported)";
            MaxFileNameText.Text = "127 characters (Unicode)";
            MaxLabelText.Text = "16 characters";
            CompatibilityText.Text = UdfVersionComboBox.SelectedIndex >= 4
                ? "Windows Vista / 7 / 10 / 11, modern Linux & macOS (Blu-ray standard)"
                : "Windows XP / Vista / 7 / 10 / 11, Linux, macOS";
        }
        else
        {
            MaxFileSizeText.Text = "4 GiB - 1 byte";
            MaxFileNameText.Text = "107 characters (Joliet Unicode)";
            MaxLabelText.Text = "32 characters";
            CompatibilityText.Text = "Universal (Windows 95–11, macOS, Linux, standalone players)";
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            var targetFs = FileSystemRadio.SelectedIndex == 1 ? DataFileSystem.Udf : DataFileSystem.IsoJoliet;
            _burner.SetFileSystemType(targetFs);
            if (targetFs == DataFileSystem.Udf && UdfVersionComboBox.SelectedIndex >= 0)
            {
                _burner.UdfVersion = (UdfVersion)UdfVersionComboBox.SelectedIndex;
            }
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ErrorInfoBar.Title = "Cannot Switch File System";
            ErrorInfoBar.Message = ex.Message;
            ErrorInfoBar.IsOpen = true;
        }
    }
}
