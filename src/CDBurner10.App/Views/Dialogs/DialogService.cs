using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed class DialogService : DefaultDialogService
{
    public override async Task ShowBurnDataDialogAsync(XamlRoot xamlRoot, DataCompilation compilation, DriveModel? preferredDrive, bool autoStart = false)
    {
        var dialog = new BurnDataDialog(compilation, preferredDrive, autoStart)
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        await dialog.ShowAsync();
    }

    public override async Task<bool> ShowFileSystemDialogAsync(XamlRoot xamlRoot, GenericDataBurner burner)
    {
        var dialog = new FileSystemDialog(burner)
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public override async Task<bool> ShowBootOptionsDialogAsync(XamlRoot xamlRoot, GenericDataBurner burner)
    {
        var dialog = new BootOptionsDialog(burner)
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public override async Task<bool> ShowDiscLabelDialogAsync(XamlRoot xamlRoot, GenericDataBurner burner)
    {
        var dialog = new DiscLabelDialog(burner)
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public override async Task<FileFilterResult?> ShowFileFilterDialogAsync(
        XamlRoot xamlRoot,
        string currentPattern,
        bool include,
        bool applyToBrowser)
    {
        var dialog = new FileFilterDialog(currentPattern, include, applyToBrowser)
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        var res = await dialog.ShowAsync();
        if (res is ContentDialogResult.Primary or ContentDialogResult.Secondary)
        {
            return dialog.GetResult();
        }
        return null;
    }

    public override async Task<int?> ShowImportSessionDialogAsync(XamlRoot xamlRoot, DriveModel? drive)
    {
        var dialog = new ImportSessionDialog(drive)
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? dialog.SelectedTrackNumber
            : null;
    }

    public override async Task ShowDiscSpanningDialogAsync(XamlRoot xamlRoot, DataCompilation compilation, DriveModel? preferredDrive)
    {
        var dialog = new DiscSpanningDialog(compilation, preferredDrive)
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        await dialog.ShowAsync();
    }

    public override async Task<bool> ShowItemPropertiesDialogAsync(XamlRoot xamlRoot, VirtualItem item)
    {
        var dialog = new PropertyDialog(item)
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public override async Task ShowFileAddErrorsDialogAsync(XamlRoot xamlRoot, IReadOnlyList<ItemAddError> errors)
    {
        var list = new ListView
        {
            MaxHeight = 280,
            SelectionMode = ListViewSelectionMode.None,
            ItemsSource = errors.Select(e => $"{Path.GetFileName(e.FilePath)}: {e.Message}").ToList()
        };

        var dialog = new ContentDialog
        {
            Title = $"Could Not Add {errors.Count} Item(s)",
            Content = list,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        await dialog.ShowAsync();
    }

    public override async Task ShowBurnAudioDialogAsync(XamlRoot xamlRoot, AudioCompilation compilation, DriveModel? preferredDrive)
    {
        var dialog = new BurnAudioDialog(compilation, preferredDrive)
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        await dialog.ShowAsync();
    }

    public override async Task<bool> ShowCdTextAndGapsDialogAsync(
        XamlRoot xamlRoot,
        AudioCompilation compilation,
        AudioTrackItem? selectedTrack)
    {
        var dialog = new CdTextDialog(compilation, selectedTrack)
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public override async Task<IReadOnlyList<string>?> ShowSplitCueDialogAsync(XamlRoot xamlRoot)
    {
        var dialog = new SplitCueDialog
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.OutputFiles.Count > 0
            ? dialog.OutputFiles
            : null;
    }

    public override async Task<IReadOnlyList<string>?> ShowRipAudioCdDialogAsync(XamlRoot xamlRoot, DriveModel? drive)
    {
        var dialog = new RipAudioCdDialog(drive)
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.RippedFiles.Count > 0
            ? dialog.RippedFiles
            : null;
    }

    public async Task ShowAboutDialogAsync(XamlRoot xamlRoot)
    {
        var dialog = new AboutDialog
        {
            XamlRoot = ResolveXamlRoot(xamlRoot)
        };
        await dialog.ShowAsync();
    }

    private static XamlRoot ResolveXamlRoot(XamlRoot? xamlRoot) =>
        xamlRoot ?? App.MainWindow?.Content?.XamlRoot
        ?? throw new InvalidOperationException("No active XamlRoot available for ContentDialog.");
}
