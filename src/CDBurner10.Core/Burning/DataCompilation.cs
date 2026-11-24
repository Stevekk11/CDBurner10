using System.Globalization;
using System.Xml;
using CDBurner10.Core.Utilities;

namespace CDBurner10.Core.Burning;

public sealed class DataCompilation
{
    public GenericDataBurner DataBurner { get; }

    public string Name
    {
        get => DataBurner.VolumeName;
        set => DataBurner.VolumeName = value;
    }

    public VirtualFolder RootFolder => DataBurner.RootFolder;

    public DataCompilation()
    {
        DataBurner = new GenericDataBurner();
    }

    public DataCompilation(GenericDataBurner burner)
    {
        DataBurner = burner;
    }

    public void Save(string filePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int total = Math.Max(1, RootFolder.FileCount + RootFolder.FolderCount);
        int saved = 0;

        var doc = new XmlDocument();
        doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", "yes"));
        doc.AppendChild(doc.CreateDocumentType("layout", "http://www.cdburnerxp.se/help/data.dtd", null, null));
        doc.AppendChild(doc.CreateProcessingInstruction("xml-stylesheet", "type='text/xsl' href='http://www.cdburnerxp.se/help/compilation.xsl'"));
        doc.AppendChild(doc.CreateComment("data compilation created by CDBurner10 (CDBurnerXP compatible)"));

        var layoutEl = doc.CreateElement("layout");
        layoutEl.SetAttribute("type", "Data");
        layoutEl.SetAttribute("version", "10.0.0.0");
        layoutEl.SetAttribute("date", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        doc.AppendChild(layoutEl);

        var optionsEl = doc.CreateElement("options");
        if (DataBurner.BootImage.Enabled)
        {
            AddTextElement(optionsEl, "BootFilePath", DataBurner.BootImage.FileName);
            AddTextElement(optionsEl, "BootPlatform", DataBurner.BootImage.Platform.ToString(CultureInfo.InvariantCulture));
            AddTextElement(optionsEl, "LoadSegmentWord", DataBurner.BootImage.LoadSegment.ToString(CultureInfo.InvariantCulture));
            AddTextElement(optionsEl, "MediaTypeEmulation", ((int)DataBurner.BootImage.EmulationType).ToString(CultureInfo.InvariantCulture));
            AddTextElement(optionsEl, "SectorCount", DataBurner.BootImage.SectorsToLoad.ToString(CultureInfo.InvariantCulture));
        }
        AddTextElement(optionsEl, "BootableEnabled", DataBurner.BootImage.Enabled ? "1" : "0");
        AddTextElement(optionsEl, "FileSystemType", ((int)DataBurner.FileSystemType).ToString(CultureInfo.InvariantCulture));
        AddTextElement(optionsEl, "UdfVersion", GenericDataBurner.MapUdfVersionLegacyInt(DataBurner.UdfVersion).ToString(CultureInfo.InvariantCulture));
        layoutEl.AppendChild(optionsEl);

        var volInfoEl = doc.CreateElement("VolumeInfo");
        AddTextElement(volInfoEl, "ApplicationID", DataBurner.ApplicationName);
        AddTextElement(volInfoEl, "PublisherID", DataBurner.PublisherName);
        layoutEl.AppendChild(volInfoEl);

        var compEl = doc.CreateElement("compilation");
        compEl.SetAttribute("name", Name);
        layoutEl.AppendChild(compEl);

        var rootDirEl = doc.CreateElement("dir");
        rootDirEl.SetAttribute("name", Name);
        rootDirEl.SetAttribute("path", "\\");
        rootDirEl.SetAttribute("realpath", string.Empty);
        compEl.AppendChild(rootDirEl);

        WriteFolderRecursive(RootFolder, rootDirEl, ref saved, total, progress, ct);

        ct.ThrowIfCancellationRequested();
        doc.Save(filePath);
        progress?.Report(100);
    }

    private static void AddTextElement(XmlElement parent, string name, string value)
    {
        var el = parent.OwnerDocument.CreateElement(name);
        el.InnerText = value;
        parent.AppendChild(el);
    }

    private static void WriteFolderRecursive(
        VirtualFolder folder,
        XmlElement parentEl,
        ref int saved,
        int total,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        foreach (var file in folder.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.IsImported) continue;

            var fileEl = parentEl.OwnerDocument.CreateElement("file");
            fileEl.SetAttribute("name", file.Name);
            fileEl.SetAttribute("path", file.SourcePath);
            fileEl.SetAttribute("hidden", file.IsHidden ? "1" : "0");
            fileEl.SetAttribute("date", file.ModificationDate.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            parentEl.AppendChild(fileEl);
            saved++;
            progress?.Report(saved * 100.0 / total);
        }

        foreach (var sub in folder.Folders)
        {
            ct.ThrowIfCancellationRequested();
            var dirEl = parentEl.OwnerDocument.CreateElement("dir");
            dirEl.SetAttribute("name", sub.Name);
            dirEl.SetAttribute("path", sub.FullVirtualPath);
            dirEl.SetAttribute("realpath", sub.SourcePath);
            dirEl.SetAttribute("hidden", sub.IsHidden ? "1" : "0");
            dirEl.SetAttribute("date", sub.ModificationDate.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            parentEl.AppendChild(dirEl);
            saved++;
            progress?.Report(saved * 100.0 / total);
            WriteFolderRecursive(sub, dirEl, ref saved, total, progress, ct);
        }
    }

    public ItemAddError[] Load(string filePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var errors = new List<ItemAddError>();
        DataBurner.Clear();

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            ValidationType = ValidationType.None
        };

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = XmlReader.Create(fs, settings);

        bool isLegacy35 = false;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.Name == "compilation")
            {
                var compName = reader.GetAttribute("name");
                if (!string.IsNullOrEmpty(compName)) Name = compName;
                break;
            }
            if (reader.Name == "ROOT")
            {
                isLegacy35 = true;
                var compName = reader.GetAttribute("CompilationName");
                if (!string.IsNullOrEmpty(compName)) Name = compName;
                break;
            }

            try
            {
                switch (reader.Name)
                {
                    case "BootFilePath":
                        DataBurner.BootImage.FileName = reader.ReadElementContentAsString();
                        break;
                    case "BootPlatform":
                        DataBurner.BootImage.Platform = reader.ReadElementContentAsInt();
                        break;
                    case "LoadSegmentWord":
                        DataBurner.BootImage.LoadSegment = reader.ReadElementContentAsInt();
                        break;
                    case "MediaTypeEmulation":
                        DataBurner.BootImage.EmulationType = (BootEmulationType)reader.ReadElementContentAsInt();
                        break;
                    case "SectorCount":
                        DataBurner.BootImage.SectorsToLoad = reader.ReadElementContentAsInt();
                        break;
                    case "BootableEnabled":
                        var bootRaw = reader.ReadElementContentAsString().Trim();
                        DataBurner.BootImage.Enabled = bootRaw == "1" || bootRaw.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "FileSystemType":
                        DataBurner.SetFileSystemType((DataFileSystem)reader.ReadElementContentAsInt());
                        break;
                    case "UdfVersion":
                        DataBurner.UdfVersion = GenericDataBurner.MapLegacyIntToUdfVersion(reader.ReadElementContentAsInt());
                        break;
                    case "ApplicationID":
                        DataBurner.ApplicationName = reader.ReadElementContentAsString();
                        break;
                    case "PublisherID":
                        DataBurner.PublisherName = reader.ReadElementContentAsString();
                        break;
                }
            }
            catch
            {
                // Ignore malformed option elements
            }
        }

        if (isLegacy35)
        {
            if (reader.ReadToDescendant("DIR"))
            {
                LoadLegacy35Recursive(reader, RootFolder, fs, errors, progress, ct);
            }
        }
        else
        {
            if (reader.ReadToDescendant("dir"))
            {
                LoadModernRecursive(reader, RootFolder, fs, errors, progress, ct);
            }
        }

        progress?.Report(100);
        return errors.ToArray();
    }

    private void LoadModernRecursive(
        XmlReader reader,
        VirtualFolder currentFolder,
        FileStream fs,
        List<ItemAddError> errors,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (fs.Length > 0) progress?.Report(fs.Position * 100.0 / fs.Length);

            if (reader.Name == "dir")
            {
                var name = reader.GetAttribute("name") ?? "Folder";
                var realPath = reader.GetAttribute("realpath") ?? string.Empty;
                var hidden = reader.GetAttribute("hidden") is "1" or "true" or "True";
                var sub = new VirtualFolder(name, currentFolder, realPath) { IsHidden = hidden };
                if (DateTime.TryParse(reader.GetAttribute("date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                {
                    sub.ModificationDate = dt;
                }
                currentFolder.Folders.Add(sub);

                if (!reader.IsEmptyElement)
                {
                    using var subTree = reader.ReadSubtree();
                    if (subTree.Read())
                    {
                        LoadModernRecursive(subTree, sub, fs, errors, progress, ct);
                    }
                }
            }
            else if (reader.Name == "file")
            {
                var srcPath = reader.GetAttribute("path") ?? string.Empty;
                var name = reader.GetAttribute("name") ?? Path.GetFileName(srcPath);
                var hidden = reader.GetAttribute("hidden") is "1" or "true" or "True";
                try
                {
                    if (!File.Exists(srcPath))
                        throw new FileNotFoundException($"File not found: '{srcPath}'", srcPath);

                    var vf = new VirtualFile(srcPath, name, currentFolder) { IsHidden = hidden };
                    if (DateTime.TryParse(reader.GetAttribute("date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    {
                        vf.ModificationDate = dt;
                    }
                    currentFolder.Files.Add(vf);
                }
                catch (Exception ex)
                {
                    errors.Add(new ItemAddError(srcPath, ex.Message, ex, DataBurner.FileNameMaxLength));
                }
            }
        }
    }

    private void LoadLegacy35Recursive(
        XmlReader reader,
        VirtualFolder currentFolder,
        FileStream fs,
        List<ItemAddError> errors,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (fs.Length > 0) progress?.Report(fs.Position * 100.0 / fs.Length);

            if (reader.Name == "DIR")
            {
                var name = reader.GetAttribute("Name") ?? "Folder";
                var realPath = reader.GetAttribute("RealPath") ?? string.Empty;
                var sub = new VirtualFolder(name, currentFolder, realPath);
                currentFolder.Folders.Add(sub);

                if (!reader.IsEmptyElement)
                {
                    using var subTree = reader.ReadSubtree();
                    if (subTree.Read())
                    {
                        LoadLegacy35Recursive(subTree, sub, fs, errors, progress, ct);
                    }
                }
            }
            else if (reader.Name == "FILE")
            {
                var srcPath = reader.GetAttribute("Path") ?? string.Empty;
                var name = reader.GetAttribute("Name") ?? Path.GetFileName(srcPath);
                try
                {
                    if (!File.Exists(srcPath))
                        throw new FileNotFoundException($"File not found: '{srcPath}'", srcPath);
                    currentFolder.Files.Add(new VirtualFile(srcPath, name, currentFolder));
                }
                catch (Exception ex)
                {
                    errors.Add(new ItemAddError(srcPath, ex.Message, ex, DataBurner.FileNameMaxLength));
                }
            }
        }
    }

    public static string ReplaceDiscLabelVariables(string? labelText)
    {
        var now = DateTime.Now;
        if (string.IsNullOrWhiteSpace(labelText))
        {
            return PathHelper.ReplaceInvalidFileNameChars(now.ToShortDateString());
        }

        string expanded = labelText
            .Replace("$user", Environment.UserName, StringComparison.OrdinalIgnoreCase)
            .Replace("$hour", now.Hour.ToString("00", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("$minute", now.Minute.ToString("00", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("$year", now.Year.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("$yr", now.ToString("yy", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("$month", now.Month.ToString("00", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("$day", now.Day.ToString("00", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("$time", now.ToShortTimeString(), StringComparison.OrdinalIgnoreCase)
            .Replace("$date", now.ToShortDateString(), StringComparison.OrdinalIgnoreCase);

        return PathHelper.ReplaceInvalidFileNameChars(expanded);
    }

    /// <summary>
    /// Refreshes file sizes, timestamps, and newly added files in source-backed folders
    /// when AppSettings.DataUpdateAutomatically is enabled.
    /// </summary>
    public ItemAddError[] RefreshFromSourcePaths()
    {
        var errors = new List<ItemAddError>();
        RefreshFolderRecursive(RootFolder, errors);
        return errors.ToArray();
    }

    private void RefreshFolderRecursive(VirtualFolder folder, List<ItemAddError> errors)
    {
        foreach (var file in folder.Files.ToList())
        {
            if (string.IsNullOrWhiteSpace(file.SourcePath)) continue;
            try
            {
                if (!File.Exists(file.SourcePath))
                {
                    errors.Add(new ItemAddError(file.SourcePath, $"Source file no longer exists: '{file.SourcePath}'"));
                    continue;
                }

                var fi = new FileInfo(file.SourcePath);
                file.FileSizeBytes = fi.Length;
                file.ModificationDate = fi.LastWriteTime;
            }
            catch (Exception ex)
            {
                errors.Add(new ItemAddError(file.SourcePath, ex.Message, ex));
            }
        }

        if (!string.IsNullOrWhiteSpace(folder.SourcePath) && Directory.Exists(folder.SourcePath))
        {
            try
            {
                foreach (var diskFile in Directory.EnumerateFiles(folder.SourcePath))
                {
                    string name = Path.GetFileName(diskFile);
                    if (folder.FindFile(name) is null)
                    {
                        folder.Files.Add(new VirtualFile(diskFile, name, folder));
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(new ItemAddError(folder.SourcePath, ex.Message, ex));
            }
        }

        foreach (var sub in folder.Folders.ToList())
        {
            RefreshFolderRecursive(sub, errors);
        }
    }
}
