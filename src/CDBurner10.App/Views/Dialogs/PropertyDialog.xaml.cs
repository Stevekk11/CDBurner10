using CDBurner10.App.Converters;
using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed partial class PropertyDialog : ContentDialog
{
    private readonly VirtualItem _item;

    public PropertyDialog(VirtualItem item)
    {
        InitializeComponent();
        _item = item;

        NameBox.Text = item.Name;
        TypeText.Text = item.IsFolder ? "Virtual Folder" : "File";
        VirtualPathText.Text = item.FullVirtualPath;

        if (item is VirtualFile vf)
        {
            SourcePathText.Text = string.IsNullOrWhiteSpace(vf.SourcePath) ? "(Imported from disc)" : vf.SourcePath;
            SizeText.Text = $"{FormatHelper.FormatBytes(vf.FileSizeBytes)} ({vf.FileSizeBytes:N0} bytes)";
        }
        else if (item is VirtualFolder vd)
        {
            SourcePathText.Text = string.IsNullOrWhiteSpace(vd.SourcePath) ? "(Virtual folder)" : vd.SourcePath;
            SizeText.Text = $"{FormatHelper.FormatBytes(vd.TotalSizeBytes)} ({vd.FileCount} files, {vd.FolderCount} folders)";
        }

        HiddenCheckBox.IsChecked = item.IsHidden;
        ModifiedDatePicker.Date = new DateTimeOffset(item.ModificationDate);
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var newName = NameBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(newName))
        {
            _item.Name = newName;
        }
        _item.IsHidden = HiddenCheckBox.IsChecked == true;
        if (ModifiedDatePicker.Date.HasValue)
        {
            var d = ModifiedDatePicker.Date.Value.DateTime;
            _item.ModificationDate = new DateTime(d.Year, d.Month, d.Day,
                _item.ModificationDate.Hour, _item.ModificationDate.Minute, _item.ModificationDate.Second);
        }
    }
}
