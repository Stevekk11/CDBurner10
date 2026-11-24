using CDBurner10.Core.Burning;
using CDBurner10.Core.Utilities;

namespace CDBurner10.Tests;

public sealed class WiredSettingsTests
{
    [Fact]
    public void DiagnosticLogger_RespectsEnabledFlagAndLevelFiltering()
    {
        using var tmp = new TempDirectory();
        try
        {
            DiagnosticLogger.Configure(enabled: false, level: 2, logDirectory: tmp.Path);
            DiagnosticLogger.Error("Test", "should not write when disabled");
            Assert.False(File.Exists(DiagnosticLogger.CurrentLogFilePath));

            // Low level (0): writes Error/Warn, skips Info/Verbose
            DiagnosticLogger.Configure(enabled: true, level: 0, logDirectory: tmp.Path);
            DiagnosticLogger.Error("Test", "err-msg");
            DiagnosticLogger.Info("Test", "inf-msg");
            DiagnosticLogger.Verbose("Test", "dbg-msg");

            string content = File.ReadAllText(DiagnosticLogger.CurrentLogFilePath);
            Assert.Contains("err-msg", content);
            Assert.DoesNotContain("inf-msg", content);
            Assert.DoesNotContain("dbg-msg", content);

            // High level (2): writes Verbose too
            DiagnosticLogger.Configure(enabled: true, level: 2, logDirectory: tmp.Path);
            DiagnosticLogger.Verbose("Test", "dbg-visible");
            content = File.ReadAllText(DiagnosticLogger.CurrentLogFilePath);
            Assert.Contains("dbg-visible", content);
        }
        finally
        {
            DiagnosticLogger.ResetConfiguration();
        }
    }

    [Fact]
    public void TruncateDiscFileName_PreservesExtensionWithinMaxLength()
    {
        string longName = new string('A', 120) + ".tar.gz";
        string truncated = GenericDataBurner.TruncateDiscFileName(longName, 107);

        Assert.Equal(107, truncated.Length);
        Assert.EndsWith(".gz", truncated);
    }

    [Fact]
    public void IsNetworkPath_DetectsUncPaths()
    {
        Assert.True(GenericDataBurner.IsNetworkPath(@"\\fileserver\share\movie.iso"));
        Assert.False(GenericDataBurner.IsNetworkPath(@"C:\Temp\movie.iso"));
    }

    [Fact]
    public void RefreshFromSourcePaths_UpdatesFileSizesAndDetectsNewFolderFiles()
    {
        using var tmp = new TempDirectory();
        string folder = Path.Combine(tmp.Path, "Docs");
        Directory.CreateDirectory(folder);
        string file1 = Path.Combine(folder, "a.txt");
        File.WriteAllText(file1, "123");

        var comp = new DataCompilation();
        comp.DataBurner.AddDirectory(folder, "\\");
        var vf = comp.RootFolder.FindFolder("Docs")!.FindFile("a.txt")!;
        Assert.Equal(3, vf.FileSizeBytes);

        // Modify existing file & add a new file on disk
        File.WriteAllText(file1, "1234567890");
        string file2 = Path.Combine(folder, "b.txt");
        File.WriteAllText(file2, "hello");

        var errors = comp.RefreshFromSourcePaths();
        Assert.Empty(errors);
        Assert.Equal(10, vf.FileSizeBytes);
        Assert.NotNull(comp.RootFolder.FindFolder("Docs")!.FindFile("b.txt"));
    }
}
