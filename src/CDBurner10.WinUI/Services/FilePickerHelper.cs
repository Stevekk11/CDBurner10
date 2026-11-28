using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CDBurner10.WinUI.Services;

public static class FilePickerHelper
{
    private static Func<nint>? _windowHandleProvider;

    public static void RegisterWindowHandleProvider(Func<nint> provider)
    {
        _windowHandleProvider = provider;
    }

    public static nint GetMainWindowHandle()
    {
        return _windowHandleProvider?.Invoke() ?? nint.Zero;
    }

    public static async Task<IReadOnlyList<string>> PickFilesAsync(
        IEnumerable<string> extensions,
        bool multiple = true,
        PickerLocationId startLocation = PickerLocationId.DocumentsLibrary)
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

        if (multiple)
        {
            var files = await picker.PickMultipleFilesAsync();
            return files?.Select(f => f.Path).ToList() ?? [];
        }

        var file = await picker.PickSingleFileAsync();
        return file is not null ? [file.Path] : [];
    }

    public static async Task<string?> PickSingleFileAsync(
        IEnumerable<string> extensions,
        PickerLocationId startLocation = PickerLocationId.DocumentsLibrary)
    {
        var result = await PickFilesAsync(extensions, multiple: false, startLocation: startLocation);
        return result.Count > 0 ? result[0] : null;
    }

    public static async Task<string?> PickFolderAsync(
        PickerLocationId startLocation = PickerLocationId.DocumentsLibrary)
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

    public static async Task<string?> PickSaveFileAsync(
        string suggestedFileName,
        IDictionary<string, IList<string>> fileTypeChoices,
        PickerLocationId startLocation = PickerLocationId.DocumentsLibrary)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = startLocation,
            SuggestedFileName = suggestedFileName
        };

        foreach (var kv in fileTypeChoices)
            picker.FileTypeChoices.Add(kv.Key, kv.Value);

        InitializeWithWindow.Initialize(picker, GetMainWindowHandle());
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}