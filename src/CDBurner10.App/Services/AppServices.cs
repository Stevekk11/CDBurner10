using System.Collections.ObjectModel;
using CDBurner10.Core.Audio;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDBurner10.App.Services;

public sealed partial class AppServices : ObservableObject, IDisposable
{
    public static AppServices Current { get; } = new();

    public SettingsService Settings => SettingsService.Default;
    public AudioPlayerService AudioPlayer { get; } = new();
    public DataCompilation ActiveDataCompilation { get; } = new();
    public AudioCompilation ActiveAudioCompilation { get; } = new();
    public ObservableCollection<DriveModel> Drives { get; } = [];

    /// <summary>
    /// Replaceable by ui-dialogs with the full WinUI 3 ContentDialog implementation.
    /// </summary>
    public IDialogService Dialogs { get; set; } = new DefaultDialogService();

    [ObservableProperty]
    private DriveModel? _selectedDrive;

    [ObservableProperty]
    private DiscInfoModel _selectedDriveDiscInfo = DiscInfoModel.Empty;

    [ObservableProperty]
    private string _engineStatusMessage = "Initializing burning engine...";

    [ObservableProperty]
    private bool _isEngineInitialized;

    /// <summary>
    /// Tracks whether the user is currently working on "Data" or "Audio" so DropBox routes files accordingly.
    /// </summary>
    public string ActiveCompilationMode { get; set; } = "Data";

    public Action<string, object?>? NavigationHandler { get; set; }

    public event EventHandler<IReadOnlyList<string>>? FilesDroppedForData;
    public event EventHandler<IReadOnlyList<string>>? FilesDroppedForAudio;
    public event EventHandler? DrivesRefreshed;

    private AppServices()
    {
        ActiveDataCompilation.Name = string.IsNullOrWhiteSpace(Settings.Current.DataLabel)
            ? "Disc"
            : DataCompilation.ReplaceDiscLabelVariables(Settings.Current.DataLabel);
        ApplyDefaultDataBurnerSettings(forceIfNonEmpty: true);
    }

    public void ApplyDefaultDataBurnerSettings(bool forceIfNonEmpty = false)
    {
        var burner = ActiveDataCompilation.DataBurner;
        if (!forceIfNonEmpty && (burner.FileCount > 0 || burner.FolderCount > 0))
            return;

        var s = Settings.Current;
        var fs = s.DataFileSystemType == 1 ? DataFileSystem.Udf : DataFileSystem.IsoJoliet;
        try
        {
            burner.SetFileSystemType(fs);
        }
        catch
        {
            // Keep current FS if a >4GiB file prevents switching to ISO9660
        }

        burner.UdfVersion = s.DataUdfVersion >= 0
            ? GenericDataBurner.MapLegacyIntToUdfVersion(s.DataUdfVersion)
            : UdfVersion.V1_02;
    }

    public void InitializeEngines()
    {
        try
        {
            BurningEngine.Initialize();
            IsEngineInitialized = BurningEngine.IsInitialized;
            EngineStatusMessage = IsEngineInitialized
                ? "IMAPI2 / SPTI ready"
                : "Burning engine unavailable";
        }
        catch (Exception ex)
        {
            IsEngineInitialized = false;
            EngineStatusMessage = $"Burning engine error: {ex.Message}";
        }

        try
        {
            BassAudioEngine.Initialize();
        }
        catch
        {
            // BASS init is best-effort; audio playback/tags will retry when used
        }

        RestoreAudioSession();
    }

    private string AudioSessionFilePath => Path.Combine(Settings.SettingsDirectory, "audio_session.axp");

    public void SaveAudioSession()
    {
        try
        {
            ActiveAudioCompilation.Save(AudioSessionFilePath, copyTempRipFiles: false);
        }
        catch
        {
            // Best-effort session persistence
        }
    }

    public void RestoreAudioSession()
    {
        try
        {
            if (File.Exists(AudioSessionFilePath))
            {
                ActiveAudioCompilation.Load(AudioSessionFilePath);
                return;
            }

            // First launch with existing temp rips: recover any .wav files already in Temp\Rip
            var ripDir = Settings.GetTempRipDirectory();
            if (Directory.Exists(ripDir))
            {
                var wavs = Directory.EnumerateFiles(ripDir, "*.wav", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (wavs.Count > 0)
                {
                    foreach (var wav in wavs)
                    {
                        try { ActiveAudioCompilation.Tracks.Add(AudioTrackItem.FromFile(wav, Settings.Current.AudioAutoCDText)); }
                        catch { }
                    }
                    SaveAudioSession();
                }
            }
        }
        catch
        {
            // Best-effort session restore
        }
    }

    public async Task RefreshDrivesAsync()
    {
        if (!IsEngineInitialized)
        {
            Drives.Clear();
            SelectedDrive = null;
            SelectedDriveDiscInfo = DiscInfoModel.Empty;
            DrivesRefreshed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var drives = await Task.Run(() =>
        {
            try
            {
                return BurningEngine.RefreshDrives();
            }
            catch
            {
                return (IReadOnlyList<DriveModel>)[];
            }
        });

        var previousLetter = SelectedDrive?.Letter;
        Drives.Clear();
        foreach (var d in drives)
            Drives.Add(d);

        SelectedDrive = Drives.FirstOrDefault(d => d.Letter.Equals(previousLetter, StringComparison.OrdinalIgnoreCase))
                        ?? Drives.FirstOrDefault();

        await RefreshSelectedDiscInfoAsync();
        DrivesRefreshed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshSelectedDiscInfoAsync()
    {
        if (SelectedDrive is null)
        {
            SelectedDriveDiscInfo = DiscInfoModel.Empty;
            return;
        }

        try
        {
            SelectedDriveDiscInfo = await SelectedDrive.RefreshDiscInfoAsync();
        }
        catch
        {
            SelectedDriveDiscInfo = DiscInfoModel.Empty;
        }
    }

    public async Task EjectSelectedDriveAsync()
    {
        if (SelectedDrive is null) return;
        try
        {
            await SelectedDrive.EjectAsync();
            await RefreshSelectedDiscInfoAsync();
        }
        catch
        {
            // Ignore tray command errors
        }
    }

    public async Task LoadSelectedDriveAsync()
    {
        if (SelectedDrive is null) return;
        try
        {
            await SelectedDrive.LoadAsync();
            await RefreshSelectedDiscInfoAsync();
        }
        catch
        {
            // Ignore tray command errors
        }
    }

    public void NavigateTo(string pageTag, object? parameter = null)
    {
        NavigationHandler?.Invoke(pageTag, parameter);
    }

    public void RouteDroppedFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        if (ActiveCompilationMode.Equals("Audio", StringComparison.OrdinalIgnoreCase))
        {
            NavigateTo("audio");
            FilesDroppedForAudio?.Invoke(this, paths);
        }
        else
        {
            NavigateTo("data");
            FilesDroppedForData?.Invoke(this, paths);
        }
    }

    public void Dispose()
    {
        SaveAudioSession();
        AudioPlayer.Dispose();
        try { BassAudioEngine.Shutdown(); } catch { }
        try { BurningEngine.Uninitialize(); } catch { }
        try { Settings.Save(); } catch { }
    }
}
