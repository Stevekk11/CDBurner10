using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CDBurner10.Upscaler.Services;

public static class FilePickerHelper
{
    public static nint GetMainWindowHandle() =>
        App.MainWindow is null ? nint.Zero : WindowNative.GetWindowHandle(App.MainWindow);

    public static async Task<string?> PickSingleFileAsync(
        IEnumerable<string> extensions,
        PickerLocationId startLocation = PickerLocationId.VideosLibrary)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = startLocation
        };

        foreach (var ext in extensions)
            picker.FileTypeFilter.Add(ext);

        if (picker.FileTypeFilter.Count == 0)
            picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, GetMainWindowHandle());
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickFolderAsync(PickerLocationId startLocation = PickerLocationId.VideosLibrary)
    {
        var picker = new FolderPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = startLocation
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, GetMainWindowHandle());
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}