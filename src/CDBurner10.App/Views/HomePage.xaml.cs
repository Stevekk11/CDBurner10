using CDBurner10.App.Services;
using CDBurner10.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CDBurner10.App.Views;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; } = new();

    public HomePage()
    {
        InitializeComponent();
        RememberLastProjectToggle.IsOn = ViewModel.RememberLastProject;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.Current.DrivesRefreshed += OnDrivesRefreshed;
        AppServices.Current.PropertyChanged += OnServicesPropertyChanged;
        ViewModel.RefreshDriveSummary();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        AppServices.Current.DrivesRefreshed -= OnDrivesRefreshed;
        AppServices.Current.PropertyChanged -= OnServicesPropertyChanged;
    }

    private void OnDrivesRefreshed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(ViewModel.RefreshDriveSummary);

    private void OnServicesPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppServices.SelectedDrive) or nameof(AppServices.SelectedDriveDiscInfo) or nameof(AppServices.EngineStatusMessage))
            DispatcherQueue.TryEnqueue(ViewModel.RefreshDriveSummary);
    }

    private void TaskCardsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HomeTaskCard card)
            AppServices.Current.NavigateTo(card.Tag);
    }

    private void QuickUtilityButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
            AppServices.Current.NavigateTo(tag);
    }

    private void RememberLastProjectToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ViewModel.RememberLastProject = RememberLastProjectToggle.IsOn;
    }

    private async void OpenCompilationButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".dxp", ".axp"]);
        if (string.IsNullOrEmpty(path)) return;

        if (path.EndsWith(".axp", StringComparison.OrdinalIgnoreCase))
            AppServices.Current.NavigateTo("audio", path);
        else
            AppServices.Current.NavigateTo("data", path);
    }
}
