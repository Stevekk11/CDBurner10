namespace CDBurner10.Upscaler.Services;

public sealed class UpscalerSettings
{
    // Appearance
    public string Theme { get; set; } = "Default";

    // Notifications & Tray
    public bool ShowNotifications { get; set; } = true;
    public bool ShowProgressNotifications { get; set; } = true;
    public bool ExitToNotify { get; set; } = false;

    // Upscaler Engine (libmpv + Shaders + ONNX)
    public int ScaleFactor { get; set; } = 2;
    public string Anime4KTier { get; set; } = "HQ"; // "HQ" or "Fast"
    public string LastPresetId { get; set; } = "Anime4K_A";
    public string LastOutputFormat { get; set; } = "mp4";
    public string MpvHwdec { get; set; } = "auto-copy"; // "auto-copy", "no"
    public string MpvRenderer { get; set; } = "gpu-next"; // "gpu-next", "gpu"
    public string MpvScaler { get; set; } = "ewa_lanczossharp";
    public string CustomLibMpvPath { get; set; } = string.Empty;
    public string OnnxProvider { get; set; } = "DirectML"; // "DirectML", "CPU"
    public int OnnxTileSize { get; set; } = 256;

    // Encoding & Output
    public string VideoCodec { get; set; } = "h264"; // "h264", "hevc"
    public string EncoderPreference { get; set; } = "Auto"; // "Auto", "Software"
    public int Crf { get; set; } = 18;
    public string EncoderPreset { get; set; } = "slow";
    public string AudioMode { get; set; } = "Auto"; // "Auto", "Aac"
    public string DefaultOutputFolder { get; set; } = string.Empty;
    public bool OverwriteExisting { get; set; } = true;

    // Logging
    public bool LoggingEnabled { get; set; } = true;
    public int LoggingLevel { get; set; } = -1; // 0 = Low, -1 = Medium, 2 = High
}