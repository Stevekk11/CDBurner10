using System.Runtime.InteropServices;

namespace CDBurner10.Core.Audio;

internal static class NativeBass
{
    public const int BASS_DEVICE_DEFAULT = 0;
    public const int BASS_STREAM_DECODE = 0x200000;
    public const int BASS_UNICODE = unchecked((int)0x80000000);
    public const int BASS_POS_BYTE = 0;
    public const int BASS_ACTIVE_STOPPED = 0;
    public const int BASS_ACTIVE_PLAYING = 1;
    public const int BASS_ACTIVE_STALLED = 2;
    public const int BASS_ACTIVE_PAUSED = 3;
    public const int BASS_ATTRIB_VOL = 2;
    public const int BASS_MIXER_ENV_VOL = 2;
    public const int BASS_CDID_TEXT = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_DEVICEINFO
    {
        public IntPtr name;
        public IntPtr driver;
        public int flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_CHANNELINFO
    {
        public int freq;
        public int chans;
        public int flags;
        public int ctype;
        public int origres;
        public int plugin;
        public int sample;
        public IntPtr filename;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_CD_INFO
    {
        public IntPtr vendor;
        public IntPtr product;
        public IntPtr rev;
        public int letter;
        public int rwflags;
        public int canopen;
        public int canlock;
        public int maxspeed;
        public int cache;
        public int cdtext;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct BASS_MIXER_NODE
    {
        public long pos;
        public float value;

        public BASS_MIXER_NODE(long pos, float value)
        {
            this.pos = pos;
            this.value = value;
        }
    }

    // bass.dll
    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_Init(int device, int freq, int flags, IntPtr win, IntPtr clsid);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_Free();

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int BASS_ErrorGetCode();

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_GetDeviceInfo(int device, out BASS_DEVICEINFO info);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern int BASS_PluginLoad(string file, int flags);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_PluginFree(int handle);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern int BASS_StreamCreateFile(bool mem, string file, long offset, long length, int flags);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_StreamFree(int handle);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_ChannelPlay(int handle, bool restart);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_ChannelPause(int handle);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_ChannelStop(int handle);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int BASS_ChannelIsActive(int handle);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern long BASS_ChannelGetLength(int handle, int mode);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern long BASS_ChannelGetPosition(int handle, int mode);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_ChannelSetPosition(int handle, long pos, int mode);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern double BASS_ChannelBytes2Seconds(int handle, long pos);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern long BASS_ChannelSeconds2Bytes(int handle, double pos);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_ChannelGetInfo(int handle, out BASS_CHANNELINFO info);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int BASS_ChannelGetData(int handle, [Out] short[] buffer, int length);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_ChannelSetAttribute(int handle, int attrib, float value);

    [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_ChannelGetAttribute(int handle, int attrib, out float value);

    // basscd.dll
    [DllImport("basscd.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_CD_GetInfo(int drive, out BASS_CD_INFO info);

    [DllImport("basscd.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int BASS_CD_StreamCreate(int drive, int track, int flags);

    [DllImport("basscd.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr BASS_CD_GetID(int drive, int id);

    // bassmix.dll
    [DllImport("bassmix.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int BASS_Mixer_StreamCreate(int freq, int chans, int flags);

    [DllImport("bassmix.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_Mixer_StreamAddChannel(int handle, int channel, int flags);

    [DllImport("bassmix.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern bool BASS_Mixer_ChannelSetEnvelope(int handle, int type, [In] BASS_MIXER_NODE[] nodes, int count);

    public static int GetDeviceCount()
    {
        int count = 0;
        while (BASS_GetDeviceInfo(count, out _))
        {
            count++;
        }
        return count;
    }

    public static int GetCdDriveCount()
    {
        int count = 0;
        while (BASS_CD_GetInfo(count, out _))
        {
            count++;
        }
        return count;
    }
}
