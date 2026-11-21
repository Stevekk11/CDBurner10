using System.Runtime.InteropServices;
using RocketDivision.StarBurnX;

namespace CDBurner10.Core.Burning;

public sealed record DriveSpeedModel(int SpeedKBps, double SpeedRatio)
{
    public string DisplayText => SpeedKBps <= 0
        ? "Maximum"
        : $"{SpeedRatio:0.#}x ({SpeedKBps:N0} KB/s)";

    public override string ToString() => DisplayText;
}

public sealed record TrackModel(
    int TrackNumber,
    bool IsAudio,
    int SizeBlocks,
    int LogicalBlockSize,
    long SizeBytes,
    TimeSpan AudioDuration);

public sealed record SessionModel(
    int SessionNumber,
    IReadOnlyList<TrackModel> Tracks)
{
    public long TotalSizeBytes => Tracks.Sum(t => t.SizeBytes);
}

public sealed record DiscInfoModel(
    STARBURN_DISC_TYPE DiscType,
    string DiscTypeDescription,
    string DiscLabel,
    long TotalSizeBytes,
    long UsedSizeBytes,
    long FreeSizeBytes,
    bool IsBlank,
    bool IsErasable,
    bool IsCd,
    bool IsNextSessionAllowed,
    bool HasDisc,
    int DvdRegionMask,
    IReadOnlyList<SessionModel> Sessions)
{
    public IReadOnlyDictionary<string, bool> RegionInfo => BurningEngine.GetRegionInfo(DvdRegionMask);

    public static DiscInfoModel Empty => new(
        STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_NONE,
        "No Disc",
        string.Empty,
        0, 0, 0,
        false, false, false, false, false,
        0,
        Array.Empty<SessionModel>());

    public static DiscInfoModel FromStarBurn(DiscInfo discInfo)
    {
        try
        {
            var discType = (STARBURN_DISC_TYPE)discInfo.DiscType;
            bool hasDisc = discInfo.DiscType != 0;
            var sessions = new List<SessionModel>();

            if (hasDisc)
            {
                try
                {
                    int sNum = 1;
                    foreach (Session session in discInfo.Sessions)
                    {
                        var tracks = new List<TrackModel>();
                        int tNum = 1;
                        foreach (Track track in session.Tracks)
                        {
                            long sizeBytes = (long)track.Size * track.LogicalBlockSize;
                            var duration = track.Audio
                                ? StarBurnExtensions.GetAudioDuration(track.Size)
                                : TimeSpan.Zero;
                            tracks.Add(new TrackModel(tNum++, track.Audio, track.Size, track.LogicalBlockSize, sizeBytes, duration));
                        }
                        sessions.Add(new SessionModel(sNum++, tracks));
                    }
                }
                catch (COMException)
                {
                    // Blank disc may have 0 readable sessions
                }
            }

            long totalBytes = (long)discInfo.TotalSize * 1024L;
            long freeBytes = (long)discInfo.AvailableSize * 1024L;
            long usedBytes = (long)discInfo.UsedSize * 1024L;
            if (usedBytes == 0 && sessions.Count > 0)
            {
                usedBytes = sessions.Sum(s => s.TotalSizeBytes);
            }

            bool isBlank = discInfo.Status == STARBURN_DISC_STATUS.STARBURN_DISC_STATUS_EMPTY;
            bool nextSessionAllowed = discInfo.Status != STARBURN_DISC_STATUS.STARBURN_DISC_STATUS_COMPLETE;

            return new DiscInfoModel(
                discType,
                BurningEngine.GetDiscDescription(discType),
                discInfo.DiscLabel ?? string.Empty,
                totalBytes,
                usedBytes,
                freeBytes,
                isBlank,
                BurningEngine.IsDiscErasable(discType),
                BurningEngine.IsCdDisc(discType),
                nextSessionAllowed,
                hasDisc,
                discInfo.DVDRegionMask,
                sessions);
        }
        catch (COMException)
        {
            return Empty;
        }
    }
}

public sealed class DriveModel
{
    public Drive RawDrive { get; }
    public int Index { get; }
    public string Letter { get; }
    public string Vendor { get; }
    public string Product { get; }
    public string Revision { get; }
    public int MaxWriteSpeed { get; }
    public int MaxReadSpeed { get; }
    public int SupportedMediaRead { get; }
    public int SupportedMediaWrite { get; }
    public bool SupportTestWrite { get; }
    public bool SupportBufferUnderrunProtection { get; }
    public bool IsWriter => SupportedMediaWrite != 0;
    public string DisplayName => $"{Letter}: {Vendor} {Product}".Trim();

    public IReadOnlyList<DriveSpeedModel> SupportedWriteSpeeds { get; private set; } = [];
    public IReadOnlyList<DriveSpeedModel> SupportedReadSpeeds { get; private set; } = [];

    public DriveModel(Drive drive, int index)
    {
        RawDrive = drive;
        Index = index;

        var info = drive.DriveInfo;
        Letter = (info.Letter ?? string.Empty).Trim().TrimEnd(':', '\\');
        Vendor = (info.Vendor ?? string.Empty).Trim();
        Product = (info.Product ?? string.Empty).Trim();
        Revision = (info.Revision ?? string.Empty).Trim();
        MaxWriteSpeed = info.MaxWriteSpeed;
        MaxReadSpeed = info.MaxReadSpeed;
        SupportedMediaRead = info.SupportedMediaRead;
        SupportedMediaWrite = info.SupportedMediaWrite;
        SupportTestWrite = info.SupportTestWrite;
        SupportBufferUnderrunProtection = info.SupportBufferUnderrunProtection;

        RefreshSpeeds();
    }

    public int State
    {
        get
        {
            try { return (int)RawDrive.State; }
            catch (COMException) { return -1; }
        }
    }

    public void RefreshSpeeds()
    {
        var writeSpeeds = new List<DriveSpeedModel> { new(0, 0) };
        try
        {
            foreach (DriveSpeed sp in RawDrive.DriveInfo.SupportedWriteSpeeds)
            {
                writeSpeeds.Add(new DriveSpeedModel(sp.Speed, sp.SpeedRatio));
            }
        }
        catch (COMException) { }
        SupportedWriteSpeeds = writeSpeeds;

        var readSpeeds = new List<DriveSpeedModel> { new(0, 0) };
        try
        {
            foreach (DriveSpeed sp in RawDrive.DriveInfo.SupportedReadSpeeds)
            {
                readSpeeds.Add(new DriveSpeedModel(sp.Speed, sp.SpeedRatio));
            }
        }
        catch (COMException) { }
        SupportedReadSpeeds = readSpeeds;
    }

    public DiscInfoModel GetDiscInfo(bool refresh = true)
    {
        try
        {
            if (refresh)
            {
                RawDrive.DiscInfo.Refresh();
                RefreshSpeeds();
            }
            return DiscInfoModel.FromStarBurn(RawDrive.DiscInfo);
        }
        catch (COMException)
        {
            return DiscInfoModel.Empty;
        }
    }

    public Task<DiscInfoModel> RefreshDiscInfoAsync() => Task.Run(() => GetDiscInfo(refresh: true));

    public void Eject() => RawDrive.Eject();
    public void Load() => RawDrive.Load();

    public Task EjectAsync() => Task.Run(() =>
    {
        try { RawDrive.Eject(); }
        catch (COMException) { }
    });

    public Task LoadAsync() => Task.Run(() =>
    {
        try { RawDrive.Load(); }
        catch (COMException) { }
    });

    public override string ToString() => DisplayName;
}
