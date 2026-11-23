using CDBurner10.Core.Burning;

namespace CDBurner10.Tests;

public sealed class DataCompilationTests
{
    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesIsoJolietBootVolumeInfoAndNestedTree()
    {
        using var tmp = new TempDirectory();
        var file1 = tmp.CreateTextFile("root.txt", "hello root");
        var file2 = tmp.CreateTextFile("sub/nested.dat", "nested content 12345");
        var bootImg = tmp.CreateFile("boot.img", new byte[2048]);
        var dxpPath = Path.Combine(tmp.Path, "test_iso.dxp");

        var comp = new DataCompilation();
        comp.Name = "MyDataDisc";
        comp.DataBurner.SetFileSystemType(DataFileSystem.IsoJoliet);
        comp.DataBurner.ApplicationName = "CDBurner10-TestApp";
        comp.DataBurner.PublisherName = "CDBurner10-Publisher";
        comp.DataBurner.BootImage.Enabled = true;
        comp.DataBurner.BootImage.FileName = bootImg;
        comp.DataBurner.BootImage.Platform = 0;
        comp.DataBurner.BootImage.LoadSegment = 0x7C0;
        comp.DataBurner.BootImage.EmulationType = BootEmulationType.NoEmulation;
        comp.DataBurner.BootImage.SectorsToLoad = 4;

        var vf1 = comp.DataBurner.AddFile(file1, "\\", "root_renamed.txt");
        vf1.IsHidden = true;
        comp.DataBurner.NewFolder("\\Docs\\Archive", hidden: true);
        comp.DataBurner.AddFile(file2, "\\Docs\\Archive", "nested.dat");

        double lastProgress = 0;
        var progress = new Progress<double>(p => lastProgress = p);
        comp.Save(dxpPath, progress);

        Assert.True(File.Exists(dxpPath));

        var loaded = new DataCompilation();
        var errors = loaded.Load(dxpPath);

        Assert.Empty(errors);
        Assert.Equal("MyDataDisc", loaded.Name);
        Assert.Equal(DataFileSystem.IsoJoliet, loaded.DataBurner.FileSystemType);
        Assert.Equal("CDBurner10-TestApp", loaded.DataBurner.ApplicationName);
        Assert.Equal("CDBurner10-Publisher", loaded.DataBurner.PublisherName);

        Assert.True(loaded.DataBurner.BootImage.Enabled);
        Assert.Equal(bootImg, loaded.DataBurner.BootImage.FileName);
        Assert.Equal(0, loaded.DataBurner.BootImage.Platform);
        Assert.Equal(0x7C0, loaded.DataBurner.BootImage.LoadSegment);
        Assert.Equal(BootEmulationType.NoEmulation, loaded.DataBurner.BootImage.EmulationType);
        Assert.Equal(4, loaded.DataBurner.BootImage.SectorsToLoad);

        Assert.Equal(2, loaded.RootFolder.FileCount);
        var loadedRootFile = loaded.RootFolder.FindFile("root_renamed.txt");
        Assert.NotNull(loadedRootFile);
        Assert.Equal(file1, loadedRootFile!.SourcePath);
        Assert.True(loadedRootFile.IsHidden);

        var docs = loaded.RootFolder.FindFolder("Docs");
        Assert.NotNull(docs);
        var archive = docs!.FindFolder("Archive");
        Assert.NotNull(archive);
        Assert.True(archive!.IsHidden);
        var nested = archive.FindFile("nested.dat");
        Assert.NotNull(nested);
        Assert.Equal(file2, nested!.SourcePath);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesUdfVersion()
    {
        using var tmp = new TempDirectory();
        var file1 = tmp.CreateTextFile("video.bin", "udf payload");
        var dxpPath = Path.Combine(tmp.Path, "test_udf.dxp");

        var comp = new DataCompilation();
        comp.Name = "UDF_ARCHIVE";
        comp.DataBurner.SetFileSystemType(DataFileSystem.Udf);
        comp.DataBurner.UdfVersion = UdfVersion.V2_50;
        comp.DataBurner.AddFile(file1, "\\");

        comp.Save(dxpPath);

        var loaded = new DataCompilation();
        var errors = loaded.Load(dxpPath);

        Assert.Empty(errors);
        Assert.Equal(DataFileSystem.Udf, loaded.DataBurner.FileSystemType);
        Assert.Equal(UdfVersion.V2_50, loaded.DataBurner.UdfVersion);
        Assert.Equal(1, loaded.RootFolder.FileCount);
    }

    [Fact]
    public void Load_LegacyV35XmlFormat_ImportsFoldersAndFiles()
    {
        using var tmp = new TempDirectory();
        var f1 = tmp.CreateTextFile("a.txt", "alpha");
        var f2 = tmp.CreateTextFile("b.txt", "beta");
        var missing = Path.Combine(tmp.Path, "missing.txt");

        string legacyXml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ROOT CompilationName="LegacyDisc35">
              <DIR Name="LegacyDisc35" RealPath="">
                <FILE Name="a.txt" Path="{f1}" />
                <DIR Name="SubDir" RealPath="">
                  <FILE Name="b_custom.txt" Path="{f2}" />
                  <FILE Name="missing.txt" Path="{missing}" />
                </DIR>
              </DIR>
            </ROOT>
            """;

        var dxpPath = tmp.CreateTextFile("legacy35.dxp", legacyXml);
        var comp = new DataCompilation();
        var errors = comp.Load(dxpPath);

        Assert.Equal("LegacyDisc35", comp.Name);
        Assert.Single(errors);
        Assert.Equal(missing, errors[0].FilePath);

        Assert.Equal(2, comp.RootFolder.FileCount);
        Assert.NotNull(comp.RootFolder.FindFile("a.txt"));
        var subDir = comp.RootFolder.FindFolder("SubDir");
        Assert.NotNull(subDir);
        Assert.NotNull(subDir!.FindFile("b_custom.txt"));
    }

    [Fact]
    public void ReplaceDiscLabelVariables_ExpandsDateYearAndUser()
    {
        var result = DataCompilation.ReplaceDiscLabelVariables("Backup_$year_$user_$date");

        Assert.Contains(DateTime.Now.Year.ToString(), result);
        Assert.Contains(Environment.UserName, result);
        Assert.DoesNotContain("$year", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$user", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$date", result, StringComparison.OrdinalIgnoreCase);

        var defaultResult = DataCompilation.ReplaceDiscLabelVariables("   ");
        Assert.False(string.IsNullOrWhiteSpace(defaultResult));
    }

    [Fact]
    public async Task GenericDataBurner_SaveIsoAsync_IsoJoliet_WritesValidSectorAlignedIso()
    {
        using var tmp = new TempDirectory();
        var file = tmp.CreateTextFile("hello.txt", "CDBurner10 IMAPI2 ISO test payload");
        var isoPath = Path.Combine(tmp.Path, "out_joliet.iso");

        var burner = new GenericDataBurner { VolumeName = "ISO_TEST:INVALID*CHARS" };
        burner.AddFile(file, "\\Docs");
        await burner.SaveIsoAsync(isoPath);

        Assert.True(File.Exists(isoPath));
        long len = new FileInfo(isoPath).Length;
        Assert.True(len >= 2048 * 16, $"Expected valid ISO size, got {len}");
        Assert.Equal(0, len % 2048);
    }

    [Fact]
    public async Task GenericDataBurner_SaveIsoAsync_Udf260_FallsBackCleanlyToUdf250()
    {
        using var tmp = new TempDirectory();
        var file = tmp.CreateTextFile("archive.dat", "UDF 2.60 fallback test payload");
        var isoPath = Path.Combine(tmp.Path, "out_udf.iso");

        var burner = new GenericDataBurner
        {
            VolumeName = "UDF260_TEST",
            UdfVersion = UdfVersion.V2_60
        };
        burner.SetFileSystemType(DataFileSystem.Udf);
        burner.AddFile(file, "\\");

        await burner.SaveIsoAsync(isoPath);

        Assert.True(File.Exists(isoPath));
        long len = new FileInfo(isoPath).Length;
        Assert.True(len >= 2048 * 16, $"Expected valid UDF ISO size, got {len}");
        Assert.Equal(0, len % 2048);
    }
}
