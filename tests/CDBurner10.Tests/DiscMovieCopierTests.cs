using CDBurner10.Core.Burning;

namespace CDBurner10.Tests;

public sealed class DiscMovieCopierTests : IDisposable
{
    private readonly string _rootDir;

    public DiscMovieCopierTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), $"CDBurner10_MoviesTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDir);
    }

    [Fact]
    public void ScanDirectory_FindsMoviesInNestedSubfoldersAndMatchesSubtitles()
    {
        var discDir = Path.Combine(_rootDir, "disc");
        var subA = Path.Combine(discDir, "Kids", "Cartoons");
        var subB = Path.Combine(discDir, "Extras");
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(subB);

        File.WriteAllBytes(Path.Combine(discDir, "root_movie.MP4"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(subA, "shrek.avi"), new byte[2048]);
        File.WriteAllText(Path.Combine(subA, "shrek.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHi");
        File.WriteAllText(Path.Combine(subA, "shrek.cs.srt"), "1\n00:00:01,000 --> 00:00:02,000\nAhoj");
        File.WriteAllBytes(Path.Combine(subB, "trailer.mkv"), new byte[512]);
        File.WriteAllText(Path.Combine(subB, "readme.txt"), "not a movie");
        File.WriteAllBytes(Path.Combine(subB, "cover.jpg"), new byte[128]);

        var found = DiscMovieCopier.ScanDirectory(discDir);

        Assert.Equal(3, found.Count);
        Assert.Contains(found, m => m.FileName == "root_movie.MP4" && m.Subfolder == string.Empty && m.SizeBytes == 1024);
        Assert.Contains(found, m => m.FileName == "trailer.mkv" && m.Subfolder == "Extras" && m.SizeBytes == 512);

        var shrek = Assert.Single(found, m => m.FileName == "shrek.avi");
        Assert.Equal(Path.Combine("Kids", "Cartoons"), shrek.Subfolder);
        Assert.Equal(2, shrek.SidecarSubtitles.Count);
    }

    [Fact]
    public async Task CopyMoviesAsync_FlatModeDisambiguatesCollisionsAndCopiesSubtitles()
    {
        var discDir = Path.Combine(_rootDir, "disc2");
        var folder1 = Path.Combine(discDir, "A");
        var folder2 = Path.Combine(discDir, "B");
        var destDir = Path.Combine(_rootDir, "dest_flat");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);

        File.WriteAllBytes(Path.Combine(folder1, "clip.avi"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(folder1, "clip.srt"), "sub1");
        File.WriteAllBytes(Path.Combine(folder2, "clip.avi"), [5, 6, 7, 8]);
        File.WriteAllText(Path.Combine(folder2, "clip.srt"), "sub2");

        var items = DiscMovieCopier.ScanDirectory(discDir);
        var result = await DiscMovieCopier.CopyMoviesAsync(
            items,
            destDir,
            preserveSubfolders: false,
            includeSubtitles: true,
            overwriteExisting: false);

        Assert.Equal(2, result.FilesCopied);
        Assert.Equal(2, result.SubtitlesCopied);
        Assert.Empty(result.FailedFiles);
        Assert.True(File.Exists(Path.Combine(destDir, "clip.avi")));
        Assert.True(File.Exists(Path.Combine(destDir, "clip.srt")));
        Assert.True(File.Exists(Path.Combine(destDir, "clip (2).avi")));
        Assert.True(File.Exists(Path.Combine(destDir, "clip (2).srt")));
    }

    [Fact]
    public async Task CopyMoviesAsync_PreserveSubfoldersRecreatesTree()
    {
        var discDir = Path.Combine(_rootDir, "disc3");
        var nested = Path.Combine(discDir, "Holiday", "2024");
        var destDir = Path.Combine(_rootDir, "dest_tree");
        Directory.CreateDirectory(nested);

        File.WriteAllBytes(Path.Combine(nested, "beach.mp4"), [10, 20, 30]);

        var items = DiscMovieCopier.ScanDirectory(discDir);
        var result = await DiscMovieCopier.CopyMoviesAsync(
            items,
            destDir,
            preserveSubfolders: true,
            includeSubtitles: false);

        Assert.Equal(1, result.FilesCopied);
        Assert.True(File.Exists(Path.Combine(destDir, "Holiday", "2024", "beach.mp4")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_rootDir)) Directory.Delete(_rootDir, recursive: true); } catch { }
    }
}
