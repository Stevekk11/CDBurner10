using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed class DialogService : DefaultDialogService
{
    public override async Task ShowBurnDataDialogAsync(XamlRoot xamlRoot, DataCompilation compilation, DriveModel? preferredDrive, bool autoStart = false)
    {
        var dialog = Prepare(new BurnDataDialog(compilation, preferredDrive, autoStart), xamlRoot);
        await dialog.ShowAsync();
    }

    public override async Task<bool> ShowFileSystemDialogAsync(XamlRoot xamlRoot, GenericDataBurner burner)
    {
        var dialog = Prepare(new FileSystemDialog(burner), xamlRoot);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public override async Task<bool> ShowBootOptionsDialogAsync(XamlRoot xamlRoot, GenericDataBurner burner)
    {
        var dialog = Prepare(new BootOptionsDialog(burner), xamlRoot);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public override async Task<bool> ShowDiscLabelDialogAsync(XamlRoot xamlRoot, GenericDataBurner burner)
    {
        var dialog = Prepare(new DiscLabelDialog(burner), xamlRoot);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public override async Task<FileFilterResult?> ShowFileFilterDialogAsync(
        XamlRoot xamlRoot,
        string currentPattern,
        bool include,
        bool applyToBrowser)
    {
        var dialog = Prepare(new FileFilterDialog(currentPattern, include, applyToBrowser), xamlRoot);
        var res = await dialog.ShowAsync();
        if (res is ContentDialogResult.Primary or ContentDialogResult.Secondary)
        {
            return dialog.GetResult();
        }
        return null;
    }

    public override async Task<int?> ShowImportSessionDialogAsync(XamlRoot xamlRoot, DriveModel? drive)
    {
        var dialog = Prepare(new ImportSessionDialog(drive), xamlRoot);
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? dialog.SelectedTrackNumber
            : null;
    }

    public override async Task ShowDiscSpanningDialogAsync(XamlRoot xamlRoot, DataCompilation compilation, DriveModel? preferredDrive)
    {
        var dialog = Prepare(new DiscSpanningDialog(compilation, preferredDrive), xamlRoot);
        await dialog.ShowAsync();
    }

    public override async Task<bool> ShowItemPropertiesDialogAsync(XamlRoot xamlRoot, VirtualItem item)
    {
        var dialog = Prepare(new PropertyDialog(item), xamlRoot);
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

        var dialog = Prepare(new ContentDialog
        {
            Title = $"Could Not Add {errors.Count} Item(s)",
            Content = list,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        }, xamlRoot);
        await dialog.ShowAsync();
    }

    public override async Task ShowBurnAudioDialogAsync(XamlRoot xamlRoot, AudioCompilation compilation, DriveModel? preferredDrive)
    {
        var dialog = Prepare(new BurnAudioDialog(compilation, preferredDrive), xamlRoot);
        await dialog.ShowAsync();
    }

    public override async Task<bool> ShowCdTextAndGapsDialogAsync(
        XamlRoot xamlRoot,
        AudioCompilation compilation,
        AudioTrackItem? selectedTrack)
    {
        var dialog = Prepare(new CdTextDialog(compilation, selectedTrack), xamlRoot);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public override async Task<IReadOnlyList<string>?> ShowSplitCueDialogAsync(XamlRoot xamlRoot)
    {
        var dialog = Prepare(new SplitCueDialog(), xamlRoot);
        return await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.OutputFiles.Count > 0
            ? dialog.OutputFiles
            : null;
    }

    public override async Task<IReadOnlyList<string>?> ShowRipAudioCdDialogAsync(XamlRoot xamlRoot, DriveModel? drive)
    {
        var dialog = Prepare(new RipAudioCdDialog(drive), xamlRoot);
        return await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.RippedFiles.Count > 0
            ? dialog.RippedFiles
            : null;
    }

    public async Task ShowAboutDialogAsync(XamlRoot xamlRoot)
    {
        var dialog = Prepare(new AboutDialog(), xamlRoot);
        await dialog.ShowAsync();
    }

    private static T Prepare<T>(T dialog, XamlRoot? xamlRoot) where T : ContentDialog
    {
        var resolvedRoot = ResolveXamlRoot(xamlRoot);
        var activeTheme = (App.MainWindow?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
        if (activeTheme == ElementTheme.Default)
            activeTheme = GetActiveTheme(resolvedRoot);

        dialog.XamlRoot = resolvedRoot;
        dialog.RequestedTheme = activeTheme;
        dialog.CornerRadius = new CornerRadius(8);
        return dialog;
    }

    private static XamlRoot ResolveXamlRoot(XamlRoot? xamlRoot) =>
        xamlRoot ?? App.MainWindow?.Content?.XamlRoot
        ?? throw new InvalidOperationException("No active XamlRoot available for ContentDialog.");
}
