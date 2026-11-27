using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed partial class AboutDialog : ContentDialog
{
    public AboutDialog()
    {
        InitializeComponent();
    }

    private void GitHubButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPage.OpenExternalUrl(SettingsPage.GitHubUrl);
    }
}
