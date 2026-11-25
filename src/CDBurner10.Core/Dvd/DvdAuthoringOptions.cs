using CDBurner10.Core.Burning;

namespace CDBurner10.Core.Dvd;

/// <summary>
/// Target writable DVD disc capacity for DVD-Video authoring.
/// </summary>
public enum DvdTargetDisc
{
    /// <summary>Single-layer DVD-5 (4.7 GB / ~4,350 MiB usable).</summary>
    Dvd5_4_7GB,

    /// <summary>Dual-layer DVD-9 (8.5 GB / ~7,900 MiB usable).</summary>
    Dvd9_8_5GB
}

/// <summary>
/// Television broadcast video standard for DVD-Video output.
/// </summary>
public enum DvdVideoStandard
{
    /// <summary>PAL (720×576 @ 25 fps, GOP 15).</summary>
    Pal,

    /// <summary>NTSC (720×480 @ 29.97 fps, GOP 18).</summary>
    Ntsc
}

/// <summary>
/// Display aspect ratio setting for encoded DVD-Video titles.
/// </summary>
public enum DvdAspectRatio
{
    /// <summary>Automatically choose 16:9 or 4:3 based on the probed source aspect ratio.</summary>
    Auto,

    /// <summary>Anamorphic widescreen 16:9.</summary>
    Widescreen16x9,

    /// <summary>Standard fullscreen 4:3.</summary>
    Standard4x3
}

/// <summary>
/// Video encoding quality preset controlling target MPEG-2 bitrate.
/// </summary>
public enum DvdQualityPreset
{
    /// <summary>Automatically calculate the highest bitrate that fits the target disc.</summary>
    BestFit,

    /// <summary>High quality (8,000 kbps target video bitrate).</summary>
    High,

    /// <summary>Medium quality (5,500 kbps target video bitrate).</summary>
    Medium,

    /// <summary>Low quality (3,500 kbps target video bitrate, longer runtime per disc).</summary>
    Low
}

/// <summary>
/// Controls which audio streams from each source video are included on the DVD.
/// </summary>
public enum DvdAudioSelection
{
    /// <summary>Include only the first audio track.</summary>
    FirstTrack,

    /// <summary>Include all audio tracks (up to the DVD-Video limit of 8).</summary>
    AllTracks
}

/// <summary>
/// Controls how subtitles are handled during MPEG-2 encoding.
/// </summary>
public enum DvdSubtitleMode
{
    /// <summary>Do not include subtitles.</summary>
    None,

    /// <summary>Include the first embedded subtitle stream from the source container.</summary>
    Embedded,

    /// <summary>Burn in an external SubRip (.srt) subtitle file.</summary>
    ExternalSrt
}

/// <summary>
/// Represents a single source video file to author as a DVD-Video title.
/// </summary>
/// <param name="FilePath">Full path to the input video file (e.g. .mp4, .mkv).</param>
/// <param name="ExternalSrtPath">Optional path to an external .srt subtitle file for this title.</param>
public sealed record DvdSourceVideo(string FilePath, string? ExternalSrtPath = null);

/// <summary>
/// Probed media metadata for a source video file.
/// </summary>
/// <param name="FilePath">Full path to the probed source file.</param>
/// <param name="Duration">Total media duration.</param>
/// <param name="Width">Video frame width in pixels.</param>
/// <param name="Height">Video frame height in pixels.</param>
/// <param name="DisplayAspectRatio">Resolved display aspect ratio (width / height).</param>
/// <param name="AudioStreamCount">Number of audio streams detected.</param>
/// <param name="SubtitleStreamCount">Number of subtitle streams detected.</param>
/// <param name="Summary">Human-readable summary of resolution, duration, and streams.</param>
/// <param name="FirstSubtitleIsBitmap">True if the first embedded subtitle stream uses a bitmap codec (e.g. PGS/VobSub).</param>
public sealed record DvdMediaInfo(
    string FilePath,
    TimeSpan Duration,
    int Width,
    int Height,
    double DisplayAspectRatio,
    int AudioStreamCount,
    int SubtitleStreamCount,
    string Summary,
    bool FirstSubtitleIsBitmap = false);

/// <summary>
/// Configuration options for authoring and burning/saving a Video DVD from media files.
/// </summary>
public sealed class DvdAuthoringOptions
{
    /// <summary>Ordered list of source video files to encode as DVD titles.</summary>
    public required IReadOnlyList<DvdSourceVideo> Sources { get; set; }

    /// <summary>Target optical burner drive (required when burning directly to disc).</summary>
    public DriveModel? Drive { get; set; }

    /// <summary>Write speed in KB/s (0 = drive maximum).</summary>
    public int WriteSpeedKBps { get; set; } = 0;

    /// <summary>Target DVD disc capacity (DVD-5 4.7 GB or DVD-9 8.5 GB).</summary>
    public DvdTargetDisc TargetDisc { get; set; } = DvdTargetDisc.Dvd5_4_7GB;

    /// <summary>Broadcast video standard (PAL or NTSC).</summary>
    public DvdVideoStandard Standard { get; set; } = DvdVideoStandard.Pal;

    /// <summary>Display aspect ratio mode (Auto, 16:9, or 4:3).</summary>
    public DvdAspectRatio AspectRatio { get; set; } = DvdAspectRatio.Auto;

    /// <summary>Video bitrate quality preset.</summary>
    public DvdQualityPreset Quality { get; set; } = DvdQualityPreset.BestFit;

    /// <summary>Audio track selection mode.</summary>
    public DvdAudioSelection AudioSelection { get; set; } = DvdAudioSelection.FirstTrack;

    /// <summary>Subtitle handling mode.</summary>
    public DvdSubtitleMode SubtitleMode { get; set; } = DvdSubtitleMode.None;

    /// <summary>UDF/ISO-9660 volume label for the authored DVD.</summary>
    public string VolumeName { get; set; } = "MY_VIDEO";

    /// <summary>Whether to generate a simple title selection menu (VMG/VTSM).</summary>
    public bool CreateMenu { get; set; } = true;

    /// <summary>Whether to verify the written disc after burning.</summary>
    public bool VerifyAfterBurn { get; set; } = true;

    /// <summary>Whether to eject the optical drive tray after burning completes.</summary>
    public bool EjectAfterBurn { get; set; } = true;
}
