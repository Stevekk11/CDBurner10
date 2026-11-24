using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;

namespace CDBurner10.App.Views;

public sealed partial class DropBoxWindow : Window
{
    public event EventHandler<IReadOnlyList<string>>? FilesDropped;

    public DropBoxWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        if (Content is FrameworkElement root && App.MainWindow?.Content is FrameworkElement mainRoot)
            root.RequestedTheme = mainRoot.RequestedTheme;
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        appWindow.Resize(new SizeInt32(310, 170));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }

    public void UpdateTarget(string targetName, string statusSummary)
    {
        TargetTitleText.Text = $"Drop files for {targetName}";
        StatusText.Text = statusSummary;
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to compilation";
            e.DragUIOverride.IsCaptionVisible = true;
            DropBorder.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropBorder.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropBorder.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count > 0)
        {
            StatusText.Text = $"Added {paths.Count} item(s)";
            FilesDropped?.Invoke(this, paths);
        }
    }

    private async void PasteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.StorageItems)) return;

        var items = await content.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count > 0)
        {
            StatusText.Text = $"Pasted {paths.Count} item(s)";
            FilesDropped?.Invoke(this, paths);
        }
    }

    private void CloseMenuItem_Click(object sender, RoutedEventArgs e) => Close();
}
