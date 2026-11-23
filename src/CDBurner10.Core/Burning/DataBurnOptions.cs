namespace CDBurner10.Core.Burning;

public enum DataFileSystem
{
    IsoJoliet = 0,
    Udf = 1
}

public enum UdfVersion
{
    V1_02,
    V1_50,
    V2_00,
    V2_01,
    V2_50,
    V2_60
}

public enum WriteMode
{
    Auto,
    TAO,
    SAO,
    DAO96
}

public enum FileDateMode
{
    Original,
    Current,
    Custom
}

public sealed class BootImageOptions
{
    public bool Enabled { get; set; }
    public string FileName { get; set; } = string.Empty;
    public BootEmulationType EmulationType { get; set; } = BootEmulationType.NoEmulation;
    public int Platform { get; set; } = 0; // 0 = 80x86, 1 = PowerPC, 2 = Mac
    public int LoadSegment { get; set; } = 0x7C0;
    public int SectorsToLoad { get; set; } = 1;

    public BootImageOptions Clone() => new()
    {
        Enabled = Enabled,
        FileName = FileName,
        EmulationType = EmulationType,
        Platform = Platform,
        LoadSegment = LoadSegment,
        SectorsToLoad = SectorsToLoad
    };
}

public sealed class DataBurnOptions
{
    public required DriveModel Drive { get; init; }
    public int WriteSpeedKBps { get; init; } = 0;
    public bool TestBurn { get; init; } = false;
    public bool FinalizeDisc { get; init; } = false;
    public bool VerifyData { get; init; } = false;
    public bool EjectAfterBurn { get; init; } = true;
    public int Copies { get; init; } = 1;
    public WriteMode Mode { get; init; } = WriteMode.Auto;
    public string? VolumeName { get; init; }
    public string? PublisherName { get; init; }
    public string? ApplicationName { get; init; }
}
