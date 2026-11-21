using System.Runtime.InteropServices;
using RocketDivision.StarBurnX;

namespace CDBurner10.Core.Burning;

public static class StarBurnExtensions
{
    public static bool IsError(this COMException ex, STARBURN_RESULT_CODES error)
    {
        if (ex.InnerException is COMException inner)
        {
            return inner.IsError(error);
        }
        return (ex.ErrorCode & 0xFFF) == (int)error;
    }

    public static bool GetIsHidden(this IDataFile file) => (file.Attributes & 2) == 2;

    public static void SetIsHidden(this IDataFile file, bool value)
    {
        if (value)
        {
            file.Attributes |= 2;
        }
        else if (file.GetIsHidden())
        {
            file.Attributes &= ~2;
        }
    }

    public static long GetTotalUsedSize(this DiscInfo discInfo)
    {
        long total = 0;
        try
        {
            foreach (Session session in discInfo.Sessions)
            {
                foreach (Track track in session.Tracks)
                {
                    total += (long)track.Size * track.LogicalBlockSize;
                }
            }
        }
        catch
        {
            // Ignore COM read failures on blank/unreadable discs
        }
        return total;
    }

    public static TimeSpan GetAudioDuration(int logicalBlocks) =>
        TimeSpan.FromSeconds(logicalBlocks / 75.0);

    public static bool IsCdMediaType(this DiscInfo discInfo) =>
        (discInfo.DiscType & 0x7E) > 0;
}
