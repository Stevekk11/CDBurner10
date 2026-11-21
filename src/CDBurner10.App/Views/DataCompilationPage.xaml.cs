using CDBurner10.App.Services;
using CDBurner10.App.ViewModels;
using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace CDBurner10.App.Views;

public sealed partial class DataCompilationPage : Page
{
    public DataCompilationViewModel ViewModel { get; } = new();

    public DataCompilationPage()
    {
        InitializeComponent();
        ViewModel.TotalSizeChanged += (_, bytes) => CapacityBar.UsedBytes = bytes;
        CapacityBar.UsedBytes = ViewModel.Burner.TotalSizeBytes;

        var savedPath = AppServices.Current.Settings.Current.DataExplorerPath;
        if (!string.IsNullOrWhiteSpace(savedPath) && Directory.Exists(savedPath))
            FileBrowser.NavigateTo(savedPath);

        FileBrowser.CurrentPathChanged += (_, path) =>
        {
            AppServices.Current.Settings.Current.DataExplorerPath = path;
        };

        ApplyBrowserFilter();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.Current.ActiveCompilationMode = "Data";
        AppServices.Current.FilesDroppedForData += OnExternalFilesDropped;
        ViewModel.RefreshAll();

        if (e.Parameter is string filePath && File.Exists(filePath))
        {
            if (filePath.EndsWith(".dxp", StringComparison.OrdinalIgnoreCase))
            {
                var errors = ViewModel.LoadDxp(filePath);
                if (errors.Length > 0 && XamlRoot is not null)
                    await AppServices.Current.Dialogs.ShowFileAddErrorsDialogAsync(XamlRoot, errors);
            }
            else
            {
                await AddPathsAndReportErrorsAsync([filePath]);
            }
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        AppServices.Current.FilesDroppedForData -= OnExternalFilesDropped;
    }

    private void OnExternalFilesDropped(object? sender, IReadOnlyList<string> paths)
    {
        DispatcherQueue.TryEnqueue(async () => await AddPathsAndReportErrorsAsync(paths));
    }

    private void ApplyBrowserFilter()
    {
        if (ViewModel.FilterApplyToBrowser && !string.IsNullOrWhiteSpace(ViewModel.FilterPattern))
        {
            FileBrowser.FilterPattern = ViewModel.FilterPattern;
            FileBrowser.FilterInclude = ViewModel.FilterInclude;
        }
        else
        {
            FileBrowser.FilterPattern = string.Empty;
        }
    }

    private async Task AddPathsAndReportErrorsAsync(IReadOnlyList<string> paths)
    {
        var errors = ViewModel.AddPaths(paths);
        if (errors.Count > 0 && XamlRoot is not null)
            await AppServices.Current.Dialogs.ShowFileAddErrorsDialogAsync(XamlRoot, errors);
    }

    private async void FileBrowser_ItemsAddRequested(object? sender, IReadOnlyList<string> paths)
    {
        await AddPathsAndReportErrorsAsync(paths);
    }

    private void DiscFolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is DiscFolderNode node)
            ViewModel.SelectFolder(node.Folder);
    }

    private void DiscItemsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.HasSelection = DiscItemsListView.SelectedItems.Count > 0;
        ViewModel.HasSingleSelection = DiscItemsListView.SelectedItems.Count == 1;
    }

    private void DiscItemsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (DiscItemsListView.SelectedItem is DiscListItem listItem && listItem.Item is VirtualFolder folder)
            ViewModel.SelectFolder(folder);
    }

    private void DiscLayout_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to Data Disc";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void DiscLayout_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count > 0)
            await AddPathsAndReportErrorsAsync(paths);
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var files = await FilePickerHelper.PickFilesAsync(["*"]);
        if (files.Count > 0)
            await AddPathsAndReportErrorsAsync(files);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await FilePickerHelper.PickFolderAsync();
        if (!string.IsNullOrEmpty(folder))
            await AddPathsAndReportErrorsAsync([folder]);
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = await AppServices.Current.Dialogs.PromptTextAsync(XamlRoot, "New Folder", "Folder name", "New Folder");
        if (!string.IsNullOrWhiteSpace(name))
            ViewModel.CreateNewFolder(name);
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = DiscItemsListView.SelectedItems.OfType<DiscListItem>().Select(i => i.Item).ToList();
        if (selected.Count > 0)
            ViewModel.RemoveItems(selected);
    }

    private async void RenameSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DiscItemsListView.SelectedItem is not DiscListItem selected) return;
        var newName = await AppServices.Current.Dialogs.PromptTextAsync(XamlRoot, "Rename Item", "New name", selected.Name);
        if (!string.IsNullOrWhiteSpace(newName))
            ViewModel.RenameItem(selected.Item, newName);
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Burner.FileCount == 0 && ViewModel.Burner.FolderCount == 0) return;
        if (await AppServices.Current.Dialogs.ConfirmAsync(XamlRoot, "Clear Compilation", "Remove all files and folders from this compilation?", "Clear"))
            ViewModel.ClearCompilation();
    }

    private async void OpenDxp_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".dxp"]);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var errors = ViewModel.LoadDxp(path);
            if (errors.Length > 0)
                await AppServices.Current.Dialogs.ShowFileAddErrorsDialogAsync(XamlRoot, errors);
        }
        catch (Exception ex)
        {
            await AppServices.Current.Dialogs.ShowMessageAsync(XamlRoot, "Open Compilation Failed", ex.Message);
        }
    }

    private async void SaveDxp_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSaveFileAsync(
            ViewModel.DiscLabel,
            new Dictionary<string, IList<string>> { ["CDBurnerXP Data Compilation"] = [".dxp"] });
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            ViewModel.SaveDxp(path);
        }
        catch (Exception ex)
        {
            await AppServices.Current.Dialogs.ShowMessageAsync(XamlRoot, "Save Failed", ex.Message);
        }
    }

    private async void SaveIso_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSaveFileAsync(
            ViewModel.DiscLabel,
            new Dictionary<string, IList<string>> { ["ISO Disc Image"] = [".iso"] });
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            await ViewModel.Burner.SaveIsoAsync(path);
            await AppServices.Current.Dialogs.ShowMessageAsync(XamlRoot, "ISO Saved", $"ISO image created successfully:\n{path}");
        }
        catch (Exception ex)
        {
            await AppServices.Current.Dialogs.ShowMessageAsync(XamlRoot, "Save ISO Failed", ex.Message);
        }
    }

    private async void ImportSession_Click(object sender, RoutedEventArgs e)
    {
        var trackNum = await AppServices.Current.Dialogs.ShowImportSessionDialogAsync(XamlRoot, AppServices.Current.SelectedDrive);
        if (trackNum is null || AppServices.Current.SelectedDrive is null) return;
        try
        {
            await Task.Run(() => ViewModel.Burner.ImportTrack(AppServices.Current.SelectedDrive, trackNum.Value));
            ViewModel.RefreshAll();
        }
        catch (Exception ex)
        {
            await AppServices.Current.Dialogs.ShowMessageAsync(XamlRoot, "Import Session Failed", ex.Message);
        }
    }

    private async void FileSystem_Click(object sender, RoutedEventArgs e)
    {
        if (await AppServices.Current.Dialogs.ShowFileSystemDialogAsync(XamlRoot, ViewModel.Burner))
            ViewModel.RefreshAll();
    }

    private async void BootOptions_Click(object sender, RoutedEventArgs e)
    {
        if (await AppServices.Current.Dialogs.ShowBootOptionsDialogAsync(XamlRoot, ViewModel.Burner))
            ViewModel.RefreshAll();
    }

    private async void DiscLabel_Click(object sender, RoutedEventArgs e)
    {
        if (await AppServices.Current.Dialogs.ShowDiscLabelDialogAsync(XamlRoot, ViewModel.Burner))
            ViewModel.RefreshAll();
    }

    private async void FileFilter_Click(object sender, RoutedEventArgs e)
    {
        var res = await AppServices.Current.Dialogs.ShowFileFilterDialogAsync(
            XamlRoot, ViewModel.FilterPattern, ViewModel.FilterInclude, ViewModel.FilterApplyToBrowser);
        if (res is null) return;

        ViewModel.FilterPattern = res.Pattern;
        ViewModel.FilterInclude = res.Include;
        ViewModel.FilterApplyToBrowser = res.ApplyToBrowser;

        var s = AppServices.Current.Settings.Current;
        s.DataFilePattern = res.Pattern;
        s.DataFilePatternInclude = res.Include;
        s.DataFilePatternShowInBrowser = res.ApplyToBrowser;
        AppServices.Current.Settings.Save();

        ApplyBrowserFilter();
    }

    private async void DiscSpanning_Click(object sender, RoutedEventArgs e)
    {
        await AppServices.Current.Dialogs.ShowDiscSpanningDialogAsync(XamlRoot, ViewModel.Compilation, AppServices.Current.SelectedDrive);
    }

    private async void ItemProperties_Click(object sender, RoutedEventArgs e)
    {
        if (DiscItemsListView.SelectedItem is not DiscListItem selected) return;
        if (await AppServices.Current.Dialogs.ShowItemPropertiesDialogAsync(XamlRoot, selected.Item))
            ViewModel.RefreshAll();
    }

    private async void BurnDisc_Click(object sender, RoutedEventArgs e)
    {
        await AppServices.Current.Dialogs.ShowBurnDataDialogAsync(XamlRoot, ViewModel.Compilation, AppServices.Current.SelectedDrive);
    }
}
