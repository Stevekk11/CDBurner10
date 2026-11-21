using System.Collections.ObjectModel;
using System.IO.Enumeration;
using CDBurner10.App.Converters;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace CDBurner10.App.Controls;

public sealed class FolderNodeItem
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string IconGlyph { get; init; } = "\uE8B7";
    public bool HasUnrealizedChildren { get; set; } = true;
    public bool ChildrenLoaded { get; set; }
    public ObservableCollection<FolderNodeItem> Children { get; } = [];
}

public sealed class FileSystemEntryItem
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public string SizeDisplay => IsDirectory ? string.Empty : FormatHelper.FormatBytes(Size);
    public string TypeDisplay { get; init; } = string.Empty;
    public DateTime LastModified { get; init; }
    public string ModifiedDisplay => LastModified.ToString("yyyy-MM-dd HH:mm");
    public string IconGlyph => IsDirectory ? "\uE8B7" : GetFileGlyph(Path.GetExtension(Name));

    private static string GetFileGlyph(string ext) => ext.ToLowerInvariant() switch
    {
        ".mp3" or ".flac" or ".wav" or ".ogg" or ".wma" or ".opus" or ".m4a" or ".ape" or ".wv" or ".mpc" or ".aif" or ".aiff" => "\uE8D6",
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".vob" or ".wmv" => "\uE714",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "\uEB9F",
        ".iso" or ".bin" or ".nrg" or ".mds" or ".cue" or ".b5i" => "\uE958",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "\uF012",
        ".exe" or ".msi" => "\uE756",
        _ => "\uE8A5"
    };
}

public sealed partial class FileBrowserControl : UserControl
{
    private readonly ObservableCollection<FolderNodeItem> _rootNodes = [];
    private readonly ObservableCollection<FileSystemEntryItem> _displayedItems = [];
    private List<FileSystemEntryItem> _allCurrentItems = [];

    private string _currentPath = string.Empty;
    private string _filterPattern = string.Empty;
    private bool _filterInclude = true;

    public event EventHandler<IReadOnlyList<string>>? ItemsAddRequested;
    public event EventHandler<string>? CurrentPathChanged;

    public string CurrentPath
    {
        get => _currentPath;
        set => NavigateTo(value);
    }

    /// <summary>
    /// Semicolon-separated wildcard filter (e.g. "*.mp3;*.flac;*.wav"). Empty means show all files.
    /// </summary>
    public string FilterPattern
    {
        get => _filterPattern;
        set
        {
            _filterPattern = value ?? string.Empty;
            ApplyFilter();
        }
    }

    public bool FilterInclude
    {
        get => _filterInclude;
        set
        {
            _filterInclude = value;
            ApplyFilter();
        }
    }

    public FileBrowserControl()
    {
        InitializeComponent();
        FolderTreeView.ItemsSource = _rootNodes;
        FileListView.ItemsSource = _displayedItems;
        PopulateRootTree();

        var defaultPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Directory.Exists(defaultPath))
            NavigateTo(defaultPath);
    }

    private void PopulateRootTree()
    {
        _rootNodes.Clear();

        void AddSpecial(string label, Environment.SpecialFolder folder, string glyph)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                _rootNodes.Add(new FolderNodeItem
                {
                    Name = label,
                    FullPath = path,
                    IconGlyph = glyph,
                    HasUnrealizedChildren = HasSubdirectories(path)
                });
            }
        }

        AddSpecial("Desktop", Environment.SpecialFolder.Desktop, "\uE8FC");
        AddSpecial("Documents", Environment.SpecialFolder.MyDocuments, "\uE8A5");
        AddSpecial("Music", Environment.SpecialFolder.MyMusic, "\uE8D6");
        AddSpecial("Pictures", Environment.SpecialFolder.MyPictures, "\uEB9F");
        AddSpecial("Videos", Environment.SpecialFolder.MyVideos, "\uE714");

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
        {
            _rootNodes.Add(new FolderNodeItem
            {
                Name = "Downloads",
                FullPath = downloads,
                IconGlyph = "\uE896",
                HasUnrealizedChildren = HasSubdirectories(downloads)
            });
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name.TrimEnd('\\')
                    : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                _rootNodes.Add(new FolderNodeItem
                {
                    Name = label,
                    FullPath = drive.RootDirectory.FullName,
                    IconGlyph = drive.DriveType == DriveType.CDRom ? "\uE958" : "\uEDA2",
                    HasUnrealizedChildren = true
                });
            }
            catch
            {
                // Ignore inaccessible drives
            }
        }
    }

    private static bool HasSubdirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Any();
        }
        catch
        {
            return false;
        }
    }

    private void FolderTreeView_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is not FolderNodeItem node || node.ChildrenLoaded) return;
        node.ChildrenLoaded = true;
        node.Children.Clear();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(node.FullPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) != 0)
                    continue;

                node.Children.Add(new FolderNodeItem
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    HasUnrealizedChildren = HasSubdirectories(info.FullName)
                });
            }
        }
        catch
        {
            // Ignore access denied
        }

        node.HasUnrealizedChildren = node.Children.Count > 0;
    }

    private void FolderTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is FolderNodeItem node)
            NavigateTo(node.FullPath);
    }

    public void NavigateTo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        _currentPath = path;
        PathTextBox.Text = path;
        LoadCurrentDirectory();
        CurrentPathChanged?.Invoke(this, path);
    }

    private void LoadCurrentDirectory()
    {
        var items = new List<FileSystemEntryItem>();
        try
        {
            var dirInfo = new DirectoryInfo(_currentPath);

            foreach (var sub in dirInfo.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if ((sub.Attributes & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) != 0)
                    continue;
                items.Add(new FileSystemEntryItem
                {
                    Name = sub.Name,
                    FullPath = sub.FullName,
                    IsDirectory = true,
                    TypeDisplay = "Folder",
                    LastModified = sub.LastWriteTime
                });
            }

            foreach (var file in dirInfo.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if ((file.Attributes & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) != 0)
                    continue;
                var ext = file.Extension.TrimStart('.').ToUpperInvariant();
                items.Add(new FileSystemEntryItem
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false,
                    Size = file.Length,
                    TypeDisplay = string.IsNullOrEmpty(ext) ? "File" : $"{ext} File",
                    LastModified = file.LastWriteTime
                });
            }
        }
        catch
        {
            // Access denied or drive removed
        }

        _allCurrentItems = items;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _displayedItems.Clear();
        var search = SearchTextBox?.Text?.Trim() ?? string.Empty;
        var patterns = string.IsNullOrWhiteSpace(_filterPattern)
            ? []
            : _filterPattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var item in _allCurrentItems)
        {
            if (!string.IsNullOrEmpty(search) && !item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!item.IsDirectory && patterns.Length > 0)
            {
                bool matches = patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, item.Name, ignoreCase: true));
                if (_filterInclude && !matches) continue;
                if (!_filterInclude && matches) continue;
            }

            _displayedItems.Add(item);
        }

        EmptyFolderText.Visibility = _displayedItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddSelectedButton.IsEnabled = FileListView.SelectedItems.Count > 0;
    }

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentPath)) return;
        var parent = Directory.GetParent(_currentPath);
        if (parent is not null)
            NavigateTo(parent.FullName);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadCurrentDirectory();

    private void PathTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            NavigateTo(PathTextBox.Text.Trim());
            e.Handled = true;
        }
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AddSelectedButton.IsEnabled = FileListView.SelectedItems.Count > 0;
    }

    private void FileListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FileListView.SelectedItem is not FileSystemEntryItem item) return;
        if (item.IsDirectory)
            NavigateTo(item.FullPath);
        else
            ItemsAddRequested?.Invoke(this, [item.FullPath]);
    }

    private void AddSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var paths = FileListView.SelectedItems
            .OfType<FileSystemEntryItem>()
            .Select(i => i.FullPath)
            .ToList();

        if (paths.Count > 0)
            ItemsAddRequested?.Invoke(this, paths);
    }

    private async void FileListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<FileSystemEntryItem>().Select(i => i.FullPath).ToList();
        if (paths.Count == 0) return;

        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetText(string.Join("\n", paths));

        var storageItems = new List<IStorageItem>();
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                    storageItems.Add(await StorageFolder.GetFolderFromPathAsync(path));
                else if (File.Exists(path))
                    storageItems.Add(await StorageFile.GetFileFromPathAsync(path));
            }
            catch
            {
                // Ignore if Storage API rejects path
            }
        }

        if (storageItems.Count > 0)
            e.Data.SetStorageItems(storageItems);
    }
}
