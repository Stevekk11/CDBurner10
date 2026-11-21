using System.Collections.ObjectModel;
using System.IO.Enumeration;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace CDBurner10.App.ViewModels;

public sealed class DiscFolderNode
{
    public string Name { get; init; } = string.Empty;
    public string IconGlyph { get; init; } = "\uE8B7";
    public VirtualFolder Folder { get; init; } = null!;
    public ObservableCollection<DiscFolderNode> Children { get; } = [];
}

public sealed class DiscListItem
{
    public VirtualItem Item { get; init; } = null!;
    public string Name => Item.Name;
    public bool IsFolder => Item.IsFolder;
    public string IconGlyph => IsFolder ? "\uE8B7" : "\uE8A5";
    public string SizeDisplay => IsFolder ? $"{((VirtualFolder)Item).FileCount} file(s)" : FormatHelper.FormatBytes(Item.SizeBytes);
    public string TypeDisplay => IsFolder
        ? "Folder"
        : (Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? $"{ext} File" : "File");
    public string ModifiedDisplay => Item.ModificationDate.ToString("yyyy-MM-dd HH:mm");
    public string SourcePath => Item switch
    {
        VirtualFile vf => vf.SourcePath,
        VirtualFolder vd => vd.SourcePath,
        _ => string.Empty
    };
    public Visibility HiddenBadgeVisibility => Item.IsHidden ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class DataCompilationViewModel : ObservableObject
{
    public DataCompilation Compilation => AppServices.Current.ActiveDataCompilation;
    public GenericDataBurner Burner => Compilation.DataBurner;

    public ObservableCollection<DiscFolderNode> FolderTreeRoots { get; } = [];
    public ObservableCollection<DiscListItem> CurrentFolderItems { get; } = [];

    [ObservableProperty]
    private VirtualFolder _currentFolder;

    [ObservableProperty]
    private string _discLabel = "Disc";

    [ObservableProperty]
    private string _currentVirtualPath = "\\";

    [ObservableProperty]
    private string _fileSystemBadge = "ISO9660 / Joliet";

    [ObservableProperty]
    private string _itemCountSummary = "0 files, 0 folders";

    [ObservableProperty]
    private string _compilationSubtitle = "Ready to add files";

    [ObservableProperty]
    private Visibility _isBootableVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _emptyLayoutVisibility = Visibility.Visible;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _hasSingleSelection;

    public string FilterPattern { get; set; } = string.Empty;
    public bool FilterInclude { get; set; } = true;
    public bool FilterApplyToBrowser { get; set; } = true;

    public event EventHandler<long>? TotalSizeChanged;

    public DataCompilationViewModel()
    {
        _currentFolder = Compilation.RootFolder;
        var s = AppServices.Current.Settings.Current;
        FilterPattern = s.DataFilePattern;
        FilterInclude = s.DataFilePatternInclude;
        FilterApplyToBrowser = s.DataFilePatternShowInBrowser;
        RefreshAll();
    }

    public void RefreshAll()
    {
        if (!IsFolderInTree(Compilation.RootFolder, CurrentFolder))
            CurrentFolder = Compilation.RootFolder;

        RebuildFolderTree();
        RebuildCurrentFolderItems();
        UpdateMetadata();
    }

    private static bool IsFolderInTree(VirtualFolder root, VirtualFolder target)
    {
        if (ReferenceEquals(root, target)) return true;
        foreach (var child in root.Folders)
            if (IsFolderInTree(child, target)) return true;
        return false;
    }

    private void RebuildFolderTree()
    {
        FolderTreeRoots.Clear();
        FolderTreeRoots.Add(BuildFolderNode(Compilation.RootFolder, isRoot: true));
    }

    private static DiscFolderNode BuildFolderNode(VirtualFolder folder, bool isRoot = false)
    {
        var node = new DiscFolderNode
        {
            Name = isRoot ? (string.IsNullOrWhiteSpace(folder.Name) ? "Disc" : folder.Name) : folder.Name,
            IconGlyph = isRoot ? "\uE958" : "\uE8B7",
            Folder = folder
        };
        foreach (var sub in folder.Folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            node.Children.Add(BuildFolderNode(sub));
        return node;
    }

    private void RebuildCurrentFolderItems()
    {
        CurrentFolderItems.Clear();

        foreach (var sub in CurrentFolder.Folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            CurrentFolderItems.Add(new DiscListItem { Item = sub });

        foreach (var file in CurrentFolder.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            CurrentFolderItems.Add(new DiscListItem { Item = file });

        CurrentVirtualPath = CurrentFolder.FullVirtualPath;
        EmptyLayoutVisibility = CurrentFolderItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMetadata()
    {
        DiscLabel = string.IsNullOrWhiteSpace(Compilation.Name) ? "Disc" : Compilation.Name;
        FileSystemBadge = Burner.FileSystemType == DataFileSystem.Udf
            ? $"UDF {Burner.UdfVersion.ToString().TrimStart('V').Replace('_', '.')}"
            : "ISO9660 / Joliet";
        IsBootableVisibility = Burner.BootImage.Enabled ? Visibility.Visible : Visibility.Collapsed;
        ItemCountSummary = $"{Burner.FileCount} file(s), {Burner.FolderCount} folder(s)";
        CompilationSubtitle = $"{DiscLabel} • {FormatHelper.FormatBytes(Burner.TotalSizeBytes)} • {FileSystemBadge}";
        TotalSizeChanged?.Invoke(this, Burner.TotalSizeBytes);
    }

    public void SelectFolder(VirtualFolder folder)
    {
        CurrentFolder = folder;
        RebuildCurrentFolderItems();
    }

    public List<ItemAddError> AddPaths(IEnumerable<string> paths)
    {
        var errors = new List<ItemAddError>();
        string targetPath = CurrentFolder.FullVirtualPath;
        Func<string, bool>? filter = BuildFileFilterPredicate();

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var dirErrors = Burner.AddDirectory(path, targetPath, fileFilter: filter);
                    errors.AddRange(dirErrors);
                }
                else if (File.Exists(path))
                {
                    if (filter is not null && !filter(path))
                        continue;
                    Burner.AddFile(path, targetPath);
                }
            }
            catch (Exception ex)
            {
                errors.Add(new ItemAddError(path, ex.Message, ex));
            }
        }

        RefreshAll();
        return errors;
    }

    private Func<string, bool>? BuildFileFilterPredicate()
    {
        if (string.IsNullOrWhiteSpace(FilterPattern)) return null;
        var patterns = FilterPattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns.Length == 0) return null;

        bool include = FilterInclude;
        return filePath =>
        {
            var name = Path.GetFileName(filePath);
            bool matches = patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));
            return include ? matches : !matches;
        };
    }

    public void CreateNewFolder(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return;
        var basePath = CurrentFolder.FullVirtualPath.TrimEnd('\\');
        var newPath = $"{basePath}\\{folderName.Trim()}";
        Burner.NewFolder(newPath);
        RefreshAll();
    }

    public void RemoveItems(IEnumerable<VirtualItem> items)
    {
        foreach (var item in items.ToList())
            Burner.RemoveItem(item);
        RefreshAll();
    }

    public void RenameItem(VirtualItem item, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        Burner.RenameItem(item, newName.Trim());
        RefreshAll();
    }

    public void ClearCompilation()
    {
        Burner.Clear();
        CurrentFolder = Compilation.RootFolder;
        RefreshAll();
    }

    public ItemAddError[] LoadDxp(string dxpPath)
    {
        var errors = Compilation.Load(dxpPath);
        CurrentFolder = Compilation.RootFolder;
        RefreshAll();
        return errors;
    }

    public void SaveDxp(string dxpPath)
    {
        Compilation.Save(dxpPath);
    }
}
