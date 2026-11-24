using CDBurner10.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views;

/// <summary>Placeholder page for authoring a Video Blu-ray disc.</summary>
public sealed partial class BluRayVideoPage : Page
{
    public BluRayVideoPage()
    {
        InitializeComponent();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path)) BdmvPathBox.Text = path;
    }

    private void Soon_Click(object sender, RoutedEventArgs e)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Warning;
        StatusInfoBar.Title = "Coming soon";
        StatusInfoBar.Message = "Video Blu-ray burning is not implemented yet.";
        StatusInfoBar.IsOpen = true;
    }
}
