using CDBurner10.Core.Settings;

namespace CDBurner10.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Defaults_AndJsonPersistenceRoundTrip_WorkCorrectly()
    {
        using var tmp = new TempDirectory();
        var svc = new SettingsService(tmp.Path);

        Assert.False(svc.IsPortableMode);
        Assert.Equal(Path.Combine(tmp.Path, "settings.json"), svc.SettingsFilePath);

        // Verify key defaults on a fresh AppSettings instance (independent of any legacy UserSettings.ini on host)
        var defaults = new AppSettings();
        Assert.Equal("Disc", defaults.DataLabel);
        Assert.True(defaults.DataBurnEject);
        Assert.True(defaults.AudioAutoCDText);
        Assert.Equal(2, defaults.AudioPauseLength);
        Assert.Equal(ReplayGainMode.None, defaults.AudioReplayGain);

        // ResetToDefaults initializes a clean settings.json in the isolated directory
        svc.ResetToDefaults();

        // Mutate & Save
        svc.Current.AppLanguage = "en-US";
        svc.Current.DataLabel = "Archive_$year";
        svc.Current.DataBurnVerify = true;
        svc.Current.AudioReplayGain = ReplayGainMode.Album;
        svc.Current.AudioCrossfadingDuration = 3.5;
        svc.Current.CopyReadRetries = 7;
        svc.Save();

        Assert.True(File.Exists(svc.SettingsFilePath));

        // Reload in a fresh instance
        var svc2 = new SettingsService(tmp.Path);
        var loaded = svc2.Load();
        Assert.Equal("en-US", loaded.AppLanguage);
        Assert.Equal("Archive_$year", loaded.DataLabel);
        Assert.True(loaded.DataBurnVerify);
        Assert.Equal(ReplayGainMode.Album, loaded.AudioReplayGain);
        Assert.Equal(3.5, loaded.AudioCrossfadingDuration);
        Assert.Equal(7, loaded.CopyReadRetries);

        // ResetToDefaults
        svc2.ResetToDefaults();
        Assert.Equal("Disc", svc2.Current.DataLabel);
        Assert.False(svc2.Current.DataBurnVerify);
        Assert.Equal(ReplayGainMode.None, svc2.Current.AudioReplayGain);
    }

    [Fact]
    public void PortableModeDetection_DetectsPortableFlagFile()
    {
        var flagPath = Path.Combine(AppContext.BaseDirectory, "portable.flag");
        bool existedBefore = File.Exists(flagPath);
        try
        {
            if (existedBefore) File.Delete(flagPath);
            var nonPortable = new SettingsService();
            Assert.False(nonPortable.IsPortableMode);

            File.WriteAllText(flagPath, "1");
            var portable = new SettingsService();
            Assert.True(portable.IsPortableMode);
            Assert.Equal(AppContext.BaseDirectory, portable.SettingsDirectory);
        }
        finally
        {
            if (!existedBefore && File.Exists(flagPath))
            {
                File.Delete(flagPath);
            }
        }
    }

    [Fact]
    public void ImportLegacyIni_ParsesAllSectionsAccurately()
    {
        using var tmp = new TempDirectory();
        var iniContent = """
            ; Legacy CDBurnerXP UserSettings.ini
            [General]
            AppLanguage=de-DE
            ExitToNotify=1
            UseSPTD=true
            LoggingEnabled=1
            LoggingLevel=3

            [Data]
            DataLabel=MyBackup
            DataBurnVerify=1
            DataBurnFinalize=true
            DataBurnEject=0
            CopyReadRetries=5
            DataFileSystemType=1
            DataUdfVersion=2

            [Iso]
            IsoEjectDisc=false
            IsoFinalize=1
            LastUsedIsoPath=D:\Images\boot.iso

            [Audio]
            AudioAutoCDText=0
            AudioReplayGain=2
            AudioPauseLength=4
            AudioBurnGapless=1
            AudioCrossfadingDuration=2.5
            CDTextEnabled=true
            """;

        var iniPath = tmp.CreateTextFile("UserSettings.ini", iniContent);
        var imported = SettingsService.ImportLegacyIni(iniPath);

        Assert.Equal("de-DE", imported.AppLanguage);
        Assert.True(imported.ExitToNotify);
        Assert.True(imported.UseSPTD);
        Assert.True(imported.LoggingEnabled);
        Assert.Equal(3, imported.LoggingLevel);

        Assert.Equal("MyBackup", imported.DataLabel);
        Assert.True(imported.DataBurnVerify);
        Assert.True(imported.DataBurnFinalize);
        Assert.False(imported.DataBurnEject);
        Assert.Equal(5, imported.CopyReadRetries);
        Assert.Equal(1, imported.DataFileSystemType);
        Assert.Equal(2, imported.DataUdfVersion);

        Assert.False(imported.IsoEjectDisc);
        Assert.True(imported.IsoFinalize);
        Assert.Equal(@"D:\Images\boot.iso", imported.LastUsedIsoPath);

        Assert.False(imported.AudioAutoCDText);
        Assert.Equal(ReplayGainMode.Album, imported.AudioReplayGain);
        Assert.Equal(4, imported.AudioPauseLength);
        Assert.True(imported.AudioBurnGapless);
        Assert.Equal(2.5, imported.AudioCrossfadingDuration);
        Assert.True(imported.CDTextEnabled);
    }
}
