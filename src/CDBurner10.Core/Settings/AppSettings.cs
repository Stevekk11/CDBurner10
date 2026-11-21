namespace CDBurner10.Core.Settings;

public enum ReplayGainMode
{
    None = 0,
    Track = 1,
    Album = 2
}

public class AppSettings
{
    // [General]
    public string AppLanguage { get; set; } = string.Empty;
    public bool ExitToNotify { get; set; } = false;
    public int ToolbarDisplayStyle { get; set; } = 2;
    public bool OnlyUseSingleUnit { get; set; } = false;
    public bool StartLastUsedProjectType { get; set; } = false;
    public string LastUsedProjectType { get; set; } = string.Empty;
    public bool LoggingEnabled { get; set; } = false;
    public int LoggingLevel { get; set; } = -1;
    public bool UseSPTD { get; set; } = false;
    public bool UseCustomTempFolder { get; set; } = false;
    public string CustomTempFolder { get; set; } = string.Empty;
    public bool AutoUpdate { get; set; } = false;
    public bool PreventDefaultDragDrop { get; set; } = false;
    public bool UseFastFolderSynchronisation { get; set; } = false;

    // [Data]
    public string DataExplorerPath { get; set; } = string.Empty;
    public bool DataExplorer { get; set; } = true;
    public bool IgnoreFileRenameRequirements { get; set; } = false;
    public bool DataCacheFilesAutomatically { get; set; } = true;
    public bool DataCacheNetworkFilesAutomatically { get; set; } = false;
    public bool DataUpdateAutomatically { get; set; } = false;
    public bool DataBurnVerify { get; set; } = false;
    public bool DataBurnFinalize { get; set; } = false;
    public bool DataUseDiscAtOnce { get; set; } = false;
    public bool DataBurnEject { get; set; } = true;
    public bool DataTileVertical { get; set; } = false;
    public bool DataMode2XA { get; set; } = false;
    public bool DataAskForSettings { get; set; } = true;
    public string DataFilePattern { get; set; } = string.Empty;
    public bool DataFilePatternInclude { get; set; } = true;
    public bool DataFilePatternShowInBrowser { get; set; } = true;
    public string DataSaveISOPath { get; set; } = string.Empty;
    public string DataVideoLayoutPath { get; set; } = string.Empty;
    public int DataExplorerViewStyle { get; set; } = 1;
    public int LastDataBurnSpeed { get; set; } = 0;
    public int DataFileSystemType { get; set; } = -1;
    public int DataUdfVersion { get; set; } = -1;
    public int DataIsoLevel { get; set; } = -1;
    public string DataLastDeviceUsed { get; set; } = string.Empty;
    public string DataFloppyImagePath { get; set; } = string.Empty;
    public string DataCopyImagePath { get; set; } = string.Empty;
    public string CopySourceDevice { get; set; } = string.Empty;
    public bool CopyDisableHardwareEC { get; set; } = false;
    public bool CopyIgnoreUnreadableData { get; set; } = false;
    public int CopyReadRetries { get; set; } = 3;
    public int CopyImageType { get; set; } = 0;
    public bool CopyEjectDisc { get; set; } = false;
    public bool CopyFinaliseDisc { get; set; } = true;
    public string DataConvertTargetPath { get; set; } = string.Empty;
    public string DataSaveCompilationPath { get; set; } = string.Empty;
    public string DataLabel { get; set; } = "Disc";
    public string DataCachePath { get; set; } = string.Empty;
    public bool DoubleLayerCompatible { get; set; } = true;
    public bool PadDvdSize { get; set; } = true;
    public bool ModifyIfoBupFiles { get; set; } = true;
    public string VideoLastDeviceUsed { get; set; } = string.Empty;
    public double DataSplitterPosition { get; set; } = 0;
    public double DataDropBoxX { get; set; } = -1;
    public double DataDropBoxY { get; set; } = -1;

    // [Iso]
    public bool IsoEjectDisc { get; set; } = true;
    public bool IsoFinalize { get; set; } = true;
    public bool IsoMode2XA { get; set; } = false;
    public string IsoLastDeviceUsed { get; set; } = string.Empty;
    public string LastUsedIsoPath { get; set; } = string.Empty;

    // [Audio]
    public string AudioExplorerPath { get; set; } = string.Empty;
    public bool AudioAutoCDText { get; set; } = true;
    public int AudioExplorerViewStyle { get; set; } = 1;
    public ReplayGainMode AudioReplayGain { get; set; } = ReplayGainMode.None;
    public bool AudioTileVertical { get; set; } = false;
    public int AudioPauseLength { get; set; } = 2;
    public bool AudioExplorer { get; set; } = true;
    public bool AudioCloseDisc { get; set; } = true;
    public bool AudioBurnEject { get; set; } = true;
    public bool AudioBurnGapless { get; set; } = false;
    public double AudioCrossfadingDuration { get; set; } = 0;
    public bool AudioShowPlayer { get; set; } = true;
    public string AudioOpenPlaylistFolder { get; set; } = string.Empty;
    public string AudioSaveCompilationPath { get; set; } = string.Empty;
    public string AudioLastDeviceUsed { get; set; } = string.Empty;
    public string AudioFilePattern { get; set; } = string.Empty;
    public bool AudioFilePatternInclude { get; set; } = true;
    public bool AudioFilePatternShowInBrowser { get; set; } = true;
    public int LastAudioBurnSpeed { get; set; } = 0;
    public bool CDTextEnabled { get; set; } = true;
    public double AudioSplitterPosition { get; set; } = 0;
}
