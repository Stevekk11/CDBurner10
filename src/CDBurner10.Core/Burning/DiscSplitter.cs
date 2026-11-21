using System.Text;
using CDBurner10.Core.Settings;

namespace CDBurner10.Core.Burning;

public enum DiscSplitAlgorithm
{
    OptimiseDiscCount,
    OptimiseFolderStructure
}

public sealed record DiscSplitProgress(int Percent, int DiscCount);

public sealed record DiscSplitResult(
    IReadOnlyList<GenericDataBurner> Discs,
    VirtualFile? FileTooBig = null,
    bool Cancelled = false);

public sealed class DiscSplitter
{
    private const long FileSystemOverheadBytes = 13_824_000L;

    public Task<DiscSplitResult> SplitAsync(
        DataCompilation compilation,
        long maxDiscSizeBytes,
        DiscSplitAlgorithm algorithm = DiscSplitAlgorithm.OptimiseDiscCount,
        bool includeTableOfContents = false,
        string tableOfContentsName = "Table of contents",
        IProgress<DiscSplitProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var files = compilation.RootFolder.EnumerateAllFiles().ToList();
            long usableSize = maxDiscSizeBytes - FileSystemOverheadBytes;

            if (includeTableOfContents)
            {
                var tocEstimate = new StringBuilder();
                foreach (var f in files) tocEstimate.AppendLine(f.FullVirtualPath);
                usableSize -= Encoding.UTF8.GetByteCount(tocEstimate.ToString()) + 2000;
            }

            foreach (var f in files)
            {
                if (f.FileSizeBytes > usableSize)
                {
                    return new DiscSplitResult(Array.Empty<GenericDataBurner>(), FileTooBig: f);
                }
            }

            if (algorithm == DiscSplitAlgorithm.OptimiseDiscCount)
            {
                files.Sort((x, y) =>
                {
                    int cmp = x.FileSizeBytes.CompareTo(y.FileSizeBytes);
                    return cmp != 0 ? cmp : string.Compare(x.SourcePath, y.SourcePath, StringComparison.OrdinalIgnoreCase);
                });
            }
            else
            {
                files.Sort((x, y) => string.Compare(y.FullVirtualPath, x.FullVirtualPath, StringComparison.OrdinalIgnoreCase));
            }

            var discs = new List<GenericDataBurner>();
            var discTocs = new List<StringBuilder>();
            int totalFiles = Math.Max(1, files.Count);

            while (files.Count > 0)
            {
                if (ct.IsCancellationRequested)
                {
                    return new DiscSplitResult(discs, Cancelled: true);
                }

                var disc = new GenericDataBurner
                {
                    VolumeName = compilation.Name,
                    UdfVersion = compilation.DataBurner.UdfVersion,
                    PublisherName = compilation.DataBurner.PublisherName,
                    ApplicationName = compilation.DataBurner.ApplicationName
                };
                disc.SetFileSystemType(compilation.DataBurner.FileSystemType);
                discs.Add(disc);

                var toc = new StringBuilder();
                discTocs.Add(toc);

                long currentBytes = 0;
                for (int i = files.Count - 1; i >= 0; i--)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return new DiscSplitResult(discs, Cancelled: true);
                    }

                    var candidate = files[i];
                    if (currentBytes + candidate.FileSizeBytes > usableSize)
                    {
                        if (algorithm == DiscSplitAlgorithm.OptimiseFolderStructure)
                            break;
                        continue;
                    }

                    disc.AddFileToLocation(candidate.SourcePath, candidate.FullVirtualPath);
                    currentBytes += candidate.FileSizeBytes;
                    toc.AppendLine(candidate.FullVirtualPath);
                    files.RemoveAt(i);

                    int pct = (int)((totalFiles - files.Count) * 100.0 / totalFiles);
                    progress?.Report(new DiscSplitProgress(pct, discs.Count));
                }
            }

            if (includeTableOfContents && discs.Count > 0)
            {
                var fullToc = new StringBuilder();
                for (int i = 0; i < discTocs.Count; i++)
                {
                    fullToc.AppendLine($"====== Disc {i + 1} ======");
                    fullToc.AppendLine(discTocs[i].ToString());
                }

                try
                {
                    var tocPath = SettingsService.Default.GetTempFileName(".txt");
                    File.WriteAllText(tocPath, fullToc.ToString(), Encoding.UTF8);
                    foreach (var disc in discs)
                    {
                        disc.AddFile(tocPath, "\\", $"{tableOfContentsName}.txt");
                    }
                }
                catch
                {
                    // Non-fatal if temp TOC write fails
                }
            }

            if (discs.Count > 0 && compilation.Name.Length <= discs[0].NameMaxLength - 4)
            {
                for (int i = 0; i < discs.Count; i++)
                {
                    discs[i].VolumeName = $"{compilation.Name} ({i + 1})";
                }
            }

            progress?.Report(new DiscSplitProgress(100, discs.Count));
            return new DiscSplitResult(discs);
        }, ct);
    }
}
