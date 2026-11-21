namespace CDBurner10.Core.Burning;

public abstract class VirtualItem
{
    public string Name { get; set; } = string.Empty;
    public VirtualFolder? Parent { get; internal set; }
    public bool IsHidden { get; set; }
    public bool IsImported { get; set; }
    public DateTime ModificationDate { get; set; } = DateTime.Now;
    public abstract bool IsFolder { get; }
    public abstract long SizeBytes { get; }

    public string FullVirtualPath
    {
        get
        {
            if (Parent is null) return "\\";
            var parentPath = Parent.FullVirtualPath;
            return parentPath == "\\" ? $"\\{Name}" : $"{parentPath}\\{Name}";
        }
    }
}

public sealed class VirtualFile : VirtualItem
{
    public string SourcePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public override bool IsFolder => false;
    public override long SizeBytes => FileSizeBytes;

    public VirtualFile() { }

    public VirtualFile(string sourcePath, string? nameOnDisc = null, VirtualFolder? parent = null)
    {
        SourcePath = sourcePath;
        Name = string.IsNullOrWhiteSpace(nameOnDisc) ? Path.GetFileName(sourcePath) : nameOnDisc;
        Parent = parent;
        if (File.Exists(sourcePath))
        {
            var fi = new FileInfo(sourcePath);
            FileSizeBytes = fi.Length;
            ModificationDate = fi.LastWriteTime;
            IsHidden = (fi.Attributes & FileAttributes.Hidden) != 0;
        }
    }
}

public sealed class VirtualFolder : VirtualItem
{
    public string SourcePath { get; set; } = string.Empty;
    public List<VirtualFile> Files { get; } = [];
    public List<VirtualFolder> Folders { get; } = [];

    public override bool IsFolder => true;

    public override long SizeBytes => TotalSizeBytes;

    public long TotalSizeBytes =>
        Files.Sum(f => f.FileSizeBytes) + Folders.Sum(d => d.TotalSizeBytes);

    public int FileCount =>
        Files.Count + Folders.Sum(d => d.FileCount);

    public int FolderCount =>
        Folders.Count + Folders.Sum(d => d.FolderCount);

    public VirtualFolder() { }

    public VirtualFolder(string name, VirtualFolder? parent = null, string sourcePath = "")
    {
        Name = name;
        Parent = parent;
        SourcePath = sourcePath;
        if (!string.IsNullOrEmpty(sourcePath) && Directory.Exists(sourcePath))
        {
            var di = new DirectoryInfo(sourcePath);
            ModificationDate = di.LastWriteTime;
            IsHidden = (di.Attributes & FileAttributes.Hidden) != 0;
        }
    }

    public VirtualFolder? FindFolder(string name) =>
        Folders.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public VirtualFile? FindFile(string name) =>
        Files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public VirtualFolder GetOrCreateSubFolder(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == "\\")
            return this;

        var current = this;
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            var next = current.FindFolder(seg);
            if (next is null)
            {
                next = new VirtualFolder(seg, current);
                current.Folders.Add(next);
            }
            current = next;
        }
        return current;
    }

    public IEnumerable<VirtualFile> EnumerateAllFiles()
    {
        foreach (var f in Files) yield return f;
        foreach (var d in Folders)
        {
            foreach (var f in d.EnumerateAllFiles()) yield return f;
        }
    }

    public void Clear()
    {
        Files.Clear();
        Folders.Clear();
    }
}
