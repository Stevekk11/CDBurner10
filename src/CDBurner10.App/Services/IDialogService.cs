using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Services;

public sealed record FileFilterResult(string Pattern, bool Include, bool ApplyToBrowser);

/// <summary>
/// Clean contract for all WinUI 3 ContentDialogs owned by ui-dialogs.
/// ui-dialogs implements this interface (e.g. DialogService) and assigns AppServices.Current.Dialogs.
/// </summary>
public interface IDialogService
{
    Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message);
    Task<bool> ConfirmAsync(XamlRoot xamlRoot, string title, string message, string primaryText = "OK", string closeText = "Cancel");
    Task<string?> PromptTextAsync(XamlRoot xamlRoot, string title, string label, string initialValue = "");

    // Data Compilation Dialogs
    Task ShowBurnDataDialogAsync(XamlRoot xamlRoot, DataCompilation compilation, DriveModel? preferredDrive);
    Task<bool> ShowFileSystemDialogAsync(XamlRoot xamlRoot, GenericDataBurner burner);
    Task<bool> ShowBootOptionsDialogAsync(XamlRoot xamlRoot, GenericDataBurner burner);
    Task<bool> ShowDiscLabelDialogAsync(XamlRoot xamlRoot, GenericDataBurner burner);
    Task<FileFilterResult?> ShowFileFilterDialogAsync(XamlRoot xamlRoot, string currentPattern, bool include, bool applyToBrowser);
    Task<int?> ShowImportSessionDialogAsync(XamlRoot xamlRoot, DriveModel? drive);
    Task ShowDiscSpanningDialogAsync(XamlRoot xamlRoot, DataCompilation compilation, DriveModel? preferredDrive);
    Task<bool> ShowItemPropertiesDialogAsync(XamlRoot xamlRoot, VirtualItem item);
    Task ShowFileAddErrorsDialogAsync(XamlRoot xamlRoot, IReadOnlyList<ItemAddError> errors);

    // Audio Compilation Dialogs
    Task ShowBurnAudioDialogAsync(XamlRoot xamlRoot, AudioCompilation compilation, DriveModel? preferredDrive);
    Task<bool> ShowCdTextAndGapsDialogAsync(XamlRoot xamlRoot, AudioCompilation compilation, AudioTrackItem? selectedTrack);
    Task<IReadOnlyList<string>?> ShowSplitCueDialogAsync(XamlRoot xamlRoot);
    Task<IReadOnlyList<string>?> ShowRipAudioCdDialogAsync(XamlRoot xamlRoot, DriveModel? drive);
}

/// <summary>
/// Built-in lightweight fallback implementation so DataCompilationPage and AudioCompilationPage
/// work cleanly even before ui-dialogs registers its rich ContentDialog suite.
/// </summary>
public class DefaultDialogService : IDialogService
{
    public virtual async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
    }

    public virtual async Task<bool> ConfirmAsync(XamlRoot xamlRoot, string title, string message, string primaryText = "OK", string closeText = "Cancel")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public virtual async Task<string?> PromptTextAsync(XamlRoot xamlRoot, string title, string label, string initialValue = "")
    {
        var box = new TextBox { Header = label, Text = initialValue };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    public virtual Task ShowBurnDataDialogAsync(XamlRoot xamlRoot, DataCompilation compilation, DriveModel? preferredDrive) =>
        ShowMessageAsync(xamlRoot, "Burn Data Disc", "Burn Data Disc dialog is not registered yet.");

    public virtual Task<bool> ShowFileSystemDialogAsync(XamlRoot xamlRoot, GenericDataBurner burner) =>
        Task.FromResult(false);

    public virtual Task<bool> ShowBootOptionsDialogAsync(XamlRoot xamlRoot, GenericDataBurner burner) =>
        Task.FromResult(false);

    public virtual async Task<bool> ShowDiscLabelDialogAsync(XamlRoot xamlRoot, GenericDataBurner burner)
    {
        var name = await PromptTextAsync(xamlRoot, "Disc Label", "Volume Label", burner.VolumeName);
        if (name is null) return false;
        burner.VolumeName = name;
        return true;
    }

    public virtual async Task<FileFilterResult?> ShowFileFilterDialogAsync(XamlRoot xamlRoot, string currentPattern, bool include, bool applyToBrowser)
    {
        var pattern = await PromptTextAsync(xamlRoot, "File Filter", "Wildcard pattern (e.g. *.jpg;*.png)", currentPattern);
        return pattern is null ? null : new FileFilterResult(pattern, include, applyToBrowser);
    }

    public virtual Task<int?> ShowImportSessionDialogAsync(XamlRoot xamlRoot, DriveModel? drive) =>
        Task.FromResult<int?>(null);

    public virtual Task ShowDiscSpanningDialogAsync(XamlRoot xamlRoot, DataCompilation compilation, DriveModel? preferredDrive) =>
        ShowMessageAsync(xamlRoot, "Disc Spanning", "Disc Spanning dialog is not registered yet.");

    public virtual Task<bool> ShowItemPropertiesDialogAsync(XamlRoot xamlRoot, VirtualItem item) =>
        Task.FromResult(false);

    public virtual Task ShowFileAddErrorsDialogAsync(XamlRoot xamlRoot, IReadOnlyList<ItemAddError> errors)
    {
        var summary = string.Join("\n", errors.Take(10).Select(e => $"• {e.FilePath}: {e.Message}"));
        if (errors.Count > 10) summary += $"\n...and {errors.Count - 10} more.";
        return ShowMessageAsync(xamlRoot, $"Could not add {errors.Count} item(s)", summary);
    }

    public virtual Task ShowBurnAudioDialogAsync(XamlRoot xamlRoot, AudioCompilation compilation, DriveModel? preferredDrive) =>
        ShowMessageAsync(xamlRoot, "Burn Audio CD", "Burn Audio CD dialog is not registered yet.");

    public virtual Task<bool> ShowCdTextAndGapsDialogAsync(XamlRoot xamlRoot, AudioCompilation compilation, AudioTrackItem? selectedTrack) =>
        Task.FromResult(false);

    public virtual Task<IReadOnlyList<string>?> ShowSplitCueDialogAsync(XamlRoot xamlRoot) =>
        Task.FromResult<IReadOnlyList<string>?>(null);

    public virtual Task<IReadOnlyList<string>?> ShowRipAudioCdDialogAsync(XamlRoot xamlRoot, DriveModel? drive) =>
        Task.FromResult<IReadOnlyList<string>?>(null);
}
