using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed partial class DiscLabelDialog : ContentDialog
{
    private readonly GenericDataBurner _burner;

    public DiscLabelDialog(GenericDataBurner burner)
    {
        InitializeComponent();
        _burner = burner;

        VolumeLabelBox.Text = burner.VolumeName;
        PublisherBox.Text = burner.PublisherName;
        ApplicationBox.Text = burner.ApplicationName;
        UpdatePreview();
    }

    private void VolumeLabelBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        string expanded = DataCompilation.ReplaceDiscLabelVariables(VolumeLabelBox.Text);
        if (expanded.Length > _burner.NameMaxLength)
            expanded = expanded[.._burner.NameMaxLength];
        LabelPreviewText.Text =
            $"Preview: \"{expanded}\" (max {_burner.NameMaxLength} chars) • Variables: $date, $year, $month, $day, $user";
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _burner.VolumeName = string.IsNullOrWhiteSpace(VolumeLabelBox.Text) ? "Disc" : VolumeLabelBox.Text.Trim();
        _burner.PublisherName = PublisherBox.Text?.Trim() ?? string.Empty;
        _burner.ApplicationName = string.IsNullOrWhiteSpace(ApplicationBox.Text) ? "CDBurner10" : ApplicationBox.Text.Trim();
    }
}
