using CDBurner10.Core.Burning;

namespace CDBurner10.Tests;

public sealed class DiscSplitterTests
{
    private const long Overhead = 13_824_000L;

    private static string CreateSizedFile(TempDirectory tmp, string relPath, int sizeBytes)
    {
        var full = Path.Combine(tmp.Path, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var fs = new FileStream(full, FileMode.Create, FileAccess.Write);
        fs.SetLength(sizeBytes);
        return full;
    }

    [Fact]
    public async Task SplitAsync_OptimiseDiscCount_PacksIntoMinimumDiscsWithToc()
    {
        using var tmp = new TempDirectory();
        var comp = new DataCompilation { Name = "Backup" };

        // 4 files: 600KB, 500KB, 400KB, 300KB. Usable space = 1,000,000 bytes (+ TOC headroom)
        // OptimiseDiscCount packs (600+400)=1000KB and (500+300)=800KB -> 2 discs
        var f1 = CreateSizedFile(tmp, "a_600.bin", 600_000);
        var f2 = CreateSizedFile(tmp, "b_500.bin", 500_000);
        var f3 = CreateSizedFile(tmp, "c_400.bin", 400_000);
        var f4 = CreateSizedFile(tmp, "d_300.bin", 300_000);

        comp.DataBurner.AddFile(f1, "\\FolderA");
        comp.DataBurner.AddFile(f2, "\\FolderB");
        comp.DataBurner.AddFile(f3, "\\FolderC");
        comp.DataBurner.AddFile(f4, "\\FolderD");

        var splitter = new DiscSplitter();
        // Give 1,010,000 usable bytes so TOC estimate (~2100 bytes) leaves > 1,000,000 bytes
        var result = await splitter.SplitAsync(
            comp,
            maxDiscSizeBytes: Overhead + 1_010_000L,
            algorithm: DiscSplitAlgorithm.OptimiseDiscCount,
            includeTableOfContents: true,
            tableOfContentsName: "DiscIndex");

        Assert.False(result.Cancelled);
        Assert.Null(result.FileTooBig);
        Assert.Equal(2, result.Discs.Count);
        Assert.Equal("Backup (1)", result.Discs[0].VolumeName);
        Assert.Equal("Backup (2)", result.Discs[1].VolumeName);

        // Each disc should contain the generated TOC file at root
        foreach (var disc in result.Discs)
        {
            var tocFile = disc.RootFolder.FindFile("DiscIndex.txt");
            Assert.NotNull(tocFile);
            Assert.True(File.Exists(tocFile!.SourcePath));
            var tocContent = await File.ReadAllTextAsync(tocFile.SourcePath);
            Assert.Contains("====== Disc 1 ======", tocContent);
            Assert.Contains("====== Disc 2 ======", tocContent);
            Assert.Contains("a_600.bin", tocContent);
            Assert.Contains("d_300.bin", tocContent);
        }
    }

    [Fact]
    public async Task SplitAsync_OptimiseFolderStructure_PreservesSequentialOrder()
    {
        using var tmp = new TempDirectory();
        var comp = new DataCompilation { Name = "Photos" };

        // Ordered by path: A (600KB), B (500KB), C (400KB), D (500KB)
        // With 1,000,000 usable bytes:
        // Disc 1: A (600KB) -> B (500KB) doesn't fit, breaks!
        // Disc 2: B (500KB) + C (400KB) = 900KB -> D (500KB) doesn't fit, breaks!
        // Disc 3: D (500KB)
        var fA = CreateSizedFile(tmp, "a.bin", 600_000);
        var fB = CreateSizedFile(tmp, "b.bin", 500_000);
        var fC = CreateSizedFile(tmp, "c.bin", 400_000);
        var fD = CreateSizedFile(tmp, "d.bin", 500_000);

        comp.DataBurner.AddFile(fA, "\\01_Jan");
        comp.DataBurner.AddFile(fB, "\\02_Feb");
        comp.DataBurner.AddFile(fC, "\\03_Mar");
        comp.DataBurner.AddFile(fD, "\\04_Apr");

        var splitter = new DiscSplitter();
        var result = await splitter.SplitAsync(
            comp,
            maxDiscSizeBytes: Overhead + 1_000_000L,
            algorithm: DiscSplitAlgorithm.OptimiseFolderStructure,
            includeTableOfContents: false);

        Assert.Null(result.FileTooBig);
        Assert.Equal(3, result.Discs.Count);
        Assert.Equal(1, result.Discs[0].FileCount);
        Assert.Equal(2, result.Discs[1].FileCount);
        Assert.Equal(1, result.Discs[2].FileCount);
    }

    [Fact]
    public async Task SplitAsync_FileExceedsUsableSize_ReturnsFileTooBig()
    {
        using var tmp = new TempDirectory();
        var comp = new DataCompilation { Name = "TooBig" };
        var huge = CreateSizedFile(tmp, "huge.bin", 2_000_000);
        comp.DataBurner.AddFile(huge, "\\");

        var splitter = new DiscSplitter();
        var result = await splitter.SplitAsync(comp, maxDiscSizeBytes: Overhead + 1_000_000L);

        Assert.Empty(result.Discs);
        Assert.NotNull(result.FileTooBig);
        Assert.Equal("huge.bin", result.FileTooBig!.Name);
    }
}
