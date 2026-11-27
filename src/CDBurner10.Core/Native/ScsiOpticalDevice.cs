using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Utilities;
using Microsoft.Win32.SafeHandles;

namespace CDBurner10.Core.Native;

/// <summary>
/// Pure-C# Win32 SPTI (IOCTL_SCSI_PASS_THROUGH_DIRECT) + Storage IOCTL interface for optical drives.
/// Works identically on x86, x64, and ARM64 Windows with zero external native DLLs.
/// Handles MMC-3/5 queries (drive capabilities, profiles, speeds, disc/session/track TOC,
/// DVD region &amp; CSS protection), tray control, linear sector grabbing, verification,
/// and Session-at-Once (SAO/DAO) Audio CD burning with CD-Text.
/// </summary>
internal static class ScsiOpticalDevice
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;
    private const uint IOCTL_STORAGE_CHECK_VERIFY2 = 0x002D0800;
    private const uint IOCTL_STORAGE_MEDIA_REMOVAL = 0x002D4804;
    private const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
    private const uint IOCTL_STORAGE_LOAD_MEDIA = 0x002D480C;
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    private const uint IOCTL_CDROM_READ_TOC_EX = 0x00024054;

    private const byte SCSI_IOCTL_DATA_OUT = 0;
    private const byte SCSI_IOCTL_DATA_IN = 1;
    private const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;

    public const int DataSectorSize = 2048;
    public const int AudioSectorSize = 2352;

    [StructLayout(LayoutKind.Sequential)]
    private struct SCSI_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SCSI_PASS_THROUGH_DIRECT_WITH_SENSE
    {
        public SCSI_PASS_THROUGH_DIRECT Sptd;
        public uint Filler;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] SenseBuf;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PREVENT_MEDIA_REMOVAL
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool PreventMediaRemoval;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    private static string NormalizeDevicePath(string driveLetter)
    {
        char letter = char.ToUpperInvariant(driveLetter.Trim().TrimEnd(':', '\\')[0]);
        return $@"\\.\{letter}:";
    }

    public static SafeFileHandle OpenDrive(string driveLetter, bool writeAccess = true)
    {
        string path = NormalizeDevicePath(driveLetter);
        uint access = writeAccess ? (GENERIC_READ | GENERIC_WRITE) : GENERIC_READ;
        var handle = CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid && writeAccess)
        {
            handle.Dispose();
            handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        }
        return handle;
    }

    public static bool SendScsiCommand(
        SafeFileHandle handle,
        byte[] cdb,
        byte[]? dataBuffer,
        byte dataDirection,
        uint timeoutSeconds,
        out byte senseKey,
        out byte asc,
        out byte ascq)
    {
        senseKey = 0;
        asc = 0;
        ascq = 0;

        GCHandle pinnedData = default;
        try
        {
            IntPtr dataPtr = IntPtr.Zero;
            uint dataLen = 0;
            if (dataBuffer is not null && dataBuffer.Length > 0)
            {
                pinnedData = GCHandle.Alloc(dataBuffer, GCHandleType.Pinned);
                dataPtr = pinnedData.AddrOfPinnedObject();
                dataLen = (uint)dataBuffer.Length;
            }

            var req = new SCSI_PASS_THROUGH_DIRECT_WITH_SENSE
            {
                Sptd = new SCSI_PASS_THROUGH_DIRECT
                {
                    Length = (ushort)Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT>(),
                    CdbLength = (byte)Math.Min(16, cdb.Length),
                    SenseInfoLength = 32,
                    DataIn = dataDirection,
                    DataTransferLength = dataLen,
                    TimeOutValue = timeoutSeconds,
                    DataBuffer = dataPtr,
                    SenseInfoOffset = (uint)Marshal.OffsetOf<SCSI_PASS_THROUGH_DIRECT_WITH_SENSE>(nameof(SCSI_PASS_THROUGH_DIRECT_WITH_SENSE.SenseBuf)),
                    Cdb = new byte[16]
                },
                SenseBuf = new byte[32]
            };
            Array.Copy(cdb, req.Sptd.Cdb, req.Sptd.CdbLength);

            int structSize = Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT_WITH_SENSE>();
            IntPtr reqPtr = Marshal.AllocHGlobal(structSize);
            try
            {
                Marshal.StructureToPtr(req, reqPtr, false);
                bool ok = DeviceIoControl(
                    handle,
                    IOCTL_SCSI_PASS_THROUGH_DIRECT,
                    reqPtr,
                    (uint)structSize,
                    reqPtr,
                    (uint)structSize,
                    out _,
                    IntPtr.Zero);

                if (!ok) return false;

                var resp = Marshal.PtrToStructure<SCSI_PASS_THROUGH_DIRECT_WITH_SENSE>(reqPtr);
                if (resp.Sptd.ScsiStatus != 0)
                {
                    senseKey = (byte)(resp.SenseBuf[2] & 0x0F);
                    asc = resp.SenseBuf[12];
                    ascq = resp.SenseBuf[13];
                    return false;
                }
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(reqPtr);
            }
        }
        finally
        {
            if (pinnedData.IsAllocated) pinnedData.Free();
        }
    }

    public static void SetTrayLock(string driveLetter, bool locked)
    {
        try
        {
            using var handle = OpenDrive(driveLetter, writeAccess: true);
            if (handle.IsInvalid) return;
            var pmr = new PREVENT_MEDIA_REMOVAL { PreventMediaRemoval = locked };
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<PREVENT_MEDIA_REMOVAL>());
            try
            {
                Marshal.StructureToPtr(pmr, ptr, false);
                DeviceIoControl(handle, IOCTL_STORAGE_MEDIA_REMOVAL, ptr, (uint)Marshal.SizeOf<PREVENT_MEDIA_REMOVAL>(), IntPtr.Zero, 0, out _, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch { }
    }

    public static void EjectTray(string driveLetter)
    {
        using var handle = OpenDrive(driveLetter, writeAccess: true);
        if (handle.IsInvalid) return;
        DeviceIoControl(handle, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    public static void LoadTray(string driveLetter)
    {
        using var handle = OpenDrive(driveLetter, writeAccess: true);
        if (handle.IsInvalid) return;
        DeviceIoControl(handle, IOCTL_STORAGE_LOAD_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    // =========================================================================
    // Drive Capabilities (MODE SENSE 10 page 0x2A + GET CONFIGURATION profiles)
    // =========================================================================

    public sealed record DriveCapabilities(
        int MaxReadSpeedKBps,
        int MaxWriteSpeedKBps,
        bool SupportsTestWrite,
        bool SupportsBufferUnderrunProtection,
        OpticalDiscType SupportedMediaRead,
        OpticalDiscType SupportedMediaWrite);

    public static DriveCapabilities QueryDriveCapabilities(string driveLetter)
    {
        int maxRead = 0;
        int maxWrite = 0;
        bool testWrite = false;
        bool bufe = false;
        OpticalDiscType readMask = OpticalDiscType.None;
        OpticalDiscType writeMask = OpticalDiscType.None;

        try
        {
            using var handle = OpenDrive(driveLetter, writeAccess: true);
            if (handle.IsInvalid)
                return new DriveCapabilities(0, 0, false, false, OpticalDiscType.None, OpticalDiscType.None);

            // 1. MODE SENSE (10) — Page 0x2A (CD/DVD Capabilities and Mechanical Status)
            var modeBuf = new byte[128];
            byte[] modeCdb = [0x5A, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)modeBuf.Length, 0x00];
            if (SendScsiCommand(handle, modeCdb, modeBuf, SCSI_IOCTL_DATA_IN, 10, out _, out _, out _))
            {
                int blockDescLen = BinaryPrimitives.ReadUInt16BigEndian(modeBuf.AsSpan(6, 2));
                int pageOffset = 8 + blockDescLen;
                if (pageOffset + 22 <= modeBuf.Length && (modeBuf[pageOffset] & 0x3F) == 0x2A)
                {
                    byte b2 = modeBuf[pageOffset + 2];
                    byte b3 = modeBuf[pageOffset + 3];
                    byte b4 = modeBuf[pageOffset + 4];

                    readMask |= OpticalDiscType.CdRom;
                    if ((b2 & 0x01) != 0) readMask |= OpticalDiscType.CdR;
                    if ((b2 & 0x02) != 0) readMask |= OpticalDiscType.CdRw;
                    if ((b2 & 0x08) != 0) readMask |= OpticalDiscType.DvdRom;
                    if ((b2 & 0x10) != 0) readMask |= OpticalDiscType.DvdR;
                    if ((b2 & 0x20) != 0) readMask |= OpticalDiscType.DvdRam;

                    if ((b3 & 0x01) != 0) writeMask |= OpticalDiscType.CdR;
                    if ((b3 & 0x02) != 0) writeMask |= OpticalDiscType.CdRw;
                    if ((b3 & 0x04) != 0) testWrite = true;
                    if ((b3 & 0x10) != 0) writeMask |= OpticalDiscType.DvdR;
                    if ((b3 & 0x20) != 0) writeMask |= OpticalDiscType.DvdRam;

                    bufe = (b4 & 0x80) != 0;

                    maxRead = BinaryPrimitives.ReadUInt16BigEndian(modeBuf.AsSpan(pageOffset + 8, 2));
                    if (pageOffset + 20 <= modeBuf.Length)
                    {
                        maxWrite = BinaryPrimitives.ReadUInt16BigEndian(modeBuf.AsSpan(pageOffset + 18, 2));
                    }
                    if (maxWrite == 0 && pageOffset + 30 <= modeBuf.Length)
                    {
                        maxWrite = BinaryPrimitives.ReadUInt16BigEndian(modeBuf.AsSpan(pageOffset + 28, 2));
                    }
                }
            }

            // 2. GET CONFIGURATION (0x46) — Feature 0x0000 (Profile List)
            var profBuf = new byte[256];
            byte[] profCdb = [0x46, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00];
            if (SendScsiCommand(handle, profCdb, profBuf, SCSI_IOCTL_DATA_IN, 10, out _, out _, out _))
            {
                int dataLen = Math.Min(profBuf.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(profBuf.AsSpan(0, 4)) + 4);
                if (dataLen >= 12)
                {
                    int addLen = profBuf[11];
                    int end = Math.Min(dataLen, 12 + addLen);
                    for (int pos = 12; pos + 4 <= end; pos += 4)
                    {
                        ushort profile = BinaryPrimitives.ReadUInt16BigEndian(profBuf.AsSpan(pos, 2));
                        ApplyProfileCapabilities(profile, ref readMask, ref writeMask);
                    }
                }
            }
        }
        catch { }

        return new DriveCapabilities(maxRead, maxWrite, testWrite, bufe, readMask, writeMask);
    }

    private static void ApplyProfileCapabilities(ushort profile, ref OpticalDiscType readMask, ref OpticalDiscType writeMask)
    {
        switch (profile)
        {
            case 0x0008: readMask |= OpticalDiscType.CdRom; break;
            case 0x0009: readMask |= OpticalDiscType.CdR; writeMask |= OpticalDiscType.CdR; break;
            case 0x000A: readMask |= OpticalDiscType.CdRw; writeMask |= OpticalDiscType.CdRw; break;
            case 0x0010: readMask |= OpticalDiscType.DvdRom; break;
            case 0x0011: readMask |= OpticalDiscType.DvdR; writeMask |= OpticalDiscType.DvdR; break;
            case 0x0012: readMask |= OpticalDiscType.DvdRam; writeMask |= OpticalDiscType.DvdRam; break;
            case 0x0013:
            case 0x0014: readMask |= OpticalDiscType.DvdRw; writeMask |= OpticalDiscType.DvdRw; break;
            case 0x0015:
            case 0x0016: readMask |= OpticalDiscType.DvdRDl; writeMask |= OpticalDiscType.DvdRDl; break;
            case 0x001A: readMask |= OpticalDiscType.DvdPlusRw; writeMask |= OpticalDiscType.DvdPlusRw; break;
            case 0x001B: readMask |= OpticalDiscType.DvdPlusR; writeMask |= OpticalDiscType.DvdPlusR; break;
            case 0x002B: readMask |= OpticalDiscType.DvdPlusRDl; writeMask |= OpticalDiscType.DvdPlusRDl; break;
            case 0x0040: readMask |= OpticalDiscType.BdRom; break;
            case 0x0041:
            case 0x0042: readMask |= OpticalDiscType.BdR | OpticalDiscType.BdRDl; writeMask |= OpticalDiscType.BdR | OpticalDiscType.BdRDl; break;
            case 0x0043: readMask |= OpticalDiscType.BdRe; writeMask |= OpticalDiscType.BdRe; break;
            case 0x0050: readMask |= OpticalDiscType.HdDvdRom; break;
            case 0x0051: readMask |= OpticalDiscType.HdDvdR; writeMask |= OpticalDiscType.HdDvdR; break;
            case 0x0052: readMask |= OpticalDiscType.HdDvdRw; writeMask |= OpticalDiscType.HdDvdRw; break;
        }
    }

    // =========================================================================
    // Disc Info, Sessions/Tracks, DVD Region & CSS Protection
    // =========================================================================

    public static bool IsUnitReady(string driveLetter)
    {
        try
        {
            using var handle = OpenDrive(driveLetter, writeAccess: true);
            if (!handle.IsInvalid)
            {
                byte[] cdb = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00]; // TEST UNIT READY
                if (SendScsiCommand(handle, cdb, null, SCSI_IOCTL_DATA_UNSPECIFIED, 5, out _, out _, out _))
                    return true;

                // Non-admin fallback: IOCTL_STORAGE_CHECK_VERIFY2 (works with GENERIC_READ)
                if (DeviceIoControl(handle, IOCTL_STORAGE_CHECK_VERIFY2, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    return true;
            }

            var di = new DriveInfo($"{char.ToUpperInvariant(driveLetter.Trim()[0])}:\\");
            return di.IsReady;
        }
        catch
        {
            return false;
        }
    }

    public static long QueryDeviceLengthBytes(string driveLetter)
    {
        try
        {
            using var handle = OpenDrive(driveLetter, writeAccess: false);
            if (handle.IsInvalid) return 0;
            IntPtr outPtr = Marshal.AllocHGlobal(sizeof(long));
            try
            {
                if (DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, outPtr, sizeof(long), out uint returned, IntPtr.Zero) && returned >= sizeof(long))
                {
                    return Marshal.ReadInt64(outPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(outPtr);
            }
        }
        catch { }
        return 0;
    }

    public static List<SessionModel> QuerySessionsViaReadTocEx(string driveLetter)
    {
        var sessions = new List<SessionModel>();
        try
        {
            using var handle = OpenDrive(driveLetter, writeAccess: false);
            if (handle.IsInvalid) return sessions;

            // CDROM_READ_TOC_EX: Format = 0x00 (TOC, LBA), SessionTrack = 1
            byte[] inBuf = [0x00, 0x01, 0x00, 0x00];
            var outBuf = new byte[4 + 100 * 8];
            GCHandle hIn = GCHandle.Alloc(inBuf, GCHandleType.Pinned);
            GCHandle hOut = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
            try
            {
                if (!DeviceIoControl(handle, IOCTL_CDROM_READ_TOC_EX, hIn.AddrOfPinnedObject(), (uint)inBuf.Length,
                        hOut.AddrOfPinnedObject(), (uint)outBuf.Length, out uint bytesReturned, IntPtr.Zero) || bytesReturned < 12)
                {
                    return sessions;
                }
            }
            finally
            {
                hIn.Free();
                hOut.Free();
            }

            int dataLen = ((outBuf[0] << 8) | outBuf[1]) + 2;
            int limit = Math.Min(outBuf.Length, dataLen);
            var rawTracks = new List<(int TrackNum, bool IsAudio, int StartLba)>();
            for (int off = 4; off + 8 <= limit; off += 8)
            {
                byte control = (byte)(outBuf[off + 1] & 0x0F);
                int trNum = outBuf[off + 2];
                int lba = (outBuf[off + 4] << 24) | (outBuf[off + 5] << 16) | (outBuf[off + 6] << 8) | outBuf[off + 7];
                bool isAudio = (control & 0x04) == 0;
                rawTracks.Add((trNum, isAudio, lba));
            }

            var tracks = new List<TrackModel>();
            for (int i = 0; i < rawTracks.Count - 1; i++)
            {
                var cur = rawTracks[i];
                if (cur.TrackNum == 0xAA) break;
                int blocks = Math.Max(0, rawTracks[i + 1].StartLba - cur.StartLba);
                int blockSize = cur.IsAudio ? AudioSectorSize : DataSectorSize;
                long sizeBytes = (long)blocks * blockSize;
                var dur = cur.IsAudio ? TimeSpan.FromSeconds(blocks / 75.0) : TimeSpan.Zero;
                tracks.Add(new TrackModel(cur.TrackNum, cur.IsAudio, blocks, blockSize, sizeBytes, dur, cur.StartLba));
            }

            if (tracks.Count > 0)
            {
                sessions.Add(new SessionModel(1, tracks));
            }
        }
        catch { }
        return sessions;
    }

    public static (DiscProtectionSystem Protection, int RegionMask) QueryDvdCopyrightInfo(string driveLetter)
    {
        try
        {
            using var handle = OpenDrive(driveLetter, writeAccess: true);
            if (handle.IsInvalid) return (DiscProtectionSystem.None, 0);

            var buf = new byte[8];
            // READ DVD STRUCTURE (0xAD), Format 0x01 = Copyright Info
            byte[] cdb = [0xAD, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00];
            if (SendScsiCommand(handle, cdb, buf, SCSI_IOCTL_DATA_IN, 5, out _, out _, out _))
            {
                // Byte 4 = Copyright Protection System Type (1 = CSS/CPPM), Byte 5 = Region Management Info
                var protection = buf[4] == 1 ? DiscProtectionSystem.Css : DiscProtectionSystem.None;
                int regionMask = buf[5];
                return (protection, regionMask);
            }
        }
        catch { }

        return (DiscProtectionSystem.None, 0);
    }

    public static OpticalDiscType MapProfileCode(ushort profile) => profile switch
    {
        0x0008 => OpticalDiscType.CdRom,
        0x0009 => OpticalDiscType.CdR,
        0x000A => OpticalDiscType.CdRw,
        0x0010 => OpticalDiscType.DvdRom,
        0x0011 => OpticalDiscType.DvdR,
        0x0012 => OpticalDiscType.DvdRam,
        0x0013 or 0x0014 => OpticalDiscType.DvdRw,
        0x0015 or 0x0016 => OpticalDiscType.DvdRDl,
        0x001A => OpticalDiscType.DvdPlusRw,
        0x001B => OpticalDiscType.DvdPlusR,
        0x002B => OpticalDiscType.DvdPlusRDl,
        0x0040 => OpticalDiscType.BdRom,
        0x0041 or 0x0042 => OpticalDiscType.BdR,
        0x0043 => OpticalDiscType.BdRe,
        0x0050 => OpticalDiscType.HdDvdRom,
        0x0051 => OpticalDiscType.HdDvdR,
        0x0052 => OpticalDiscType.HdDvdRw,
        _ => OpticalDiscType.None
    };

    public static OpticalDiscType QueryCurrentMediaProfile(string driveLetter)
    {
        try
        {
            using var handle = OpenDrive(driveLetter, writeAccess: true);
            if (handle.IsInvalid) return OpticalDiscType.None;

            var buf = new byte[8];
            byte[] cdb = [0x46, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00];
            if (SendScsiCommand(handle, cdb, buf, SCSI_IOCTL_DATA_IN, 5, out _, out _, out _))
            {
                ushort currentProfile = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6, 2));
                return MapProfileCode(currentProfile);
            }
        }
        catch { }
        return OpticalDiscType.None;
    }

    public sealed record RawDiscLayout(
        bool IsBlank,
        bool IsErasable,
        bool IsFinalized,
        IReadOnlyList<SessionModel> Sessions,
        long UsedBytes,
        long FreeBytes,
        long TotalBytes);

    public static RawDiscLayout QueryDiscLayout(string driveLetter)
    {
        var sessions = new List<SessionModel>();
        bool isBlank = false;
        bool isErasable = false;
        bool isFinalized = false;
        long usedSectors = 0;
        long freeSectors = 0;

        try
        {
            using var handle = OpenDrive(driveLetter, writeAccess: true);
            if (handle.IsInvalid)
                return new RawDiscLayout(false, false, false, sessions, 0, 0, 0);

            // READ DISC INFORMATION (0x51)
            var discBuf = new byte[34];
            byte[] discCdb = [0x51, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)discBuf.Length, 0x00];
            if (SendScsiCommand(handle, discCdb, discBuf, SCSI_IOCTL_DATA_IN, 10, out _, out _, out _))
            {
                int discStatus = discBuf[2] & 0x03; // 0=Empty, 1=Incomplete/Appendable, 2=Complete
                isBlank = discStatus == 0;
                isFinalized = discStatus == 2;
                isErasable = (discBuf[2] & 0x10) != 0;

                int firstTrack = discBuf[3];
                int numSessions = discBuf[4] | (discBuf[9] << 8);
                int firstTrackLastSession = discBuf[5] | (discBuf[10] << 8);
                int lastTrackLastSession = discBuf[6] | (discBuf[11] << 8);

                var tracksBySession = new SortedDictionary<int, List<TrackModel>>();

                if (firstTrack > 0 && lastTrackLastSession >= firstTrack && lastTrackLastSession <= 99)
                {
                    for (int t = firstTrack; t <= lastTrackLastSession; t++)
                    {
                        var tiBuf = new byte[36];
                        // READ TRACK INFORMATION (0x52), Address/Number Type = 1 (Logical Track Number)
                        byte[] tiCdb = [0x52, 0x01, 0x00, 0x00, (byte)(t >> 8), (byte)(t & 0xFF), 0x00, 0x00, (byte)tiBuf.Length, 0x00];
                        if (!SendScsiCommand(handle, tiCdb, tiBuf, SCSI_IOCTL_DATA_IN, 5, out _, out _, out _))
                            continue;

                        int sessionNum = tiBuf[3] | (tiBuf[33] << 8);
                        if (sessionNum <= 0) sessionNum = 1;
                        byte trackMode = (byte)(tiBuf[5] & 0x0F);
                        bool isAudio = (trackMode & 0x04) == 0;
                        bool isBlankTrack = (tiBuf[6] & 0x40) != 0;
                        int startLba = (int)BinaryPrimitives.ReadUInt32BigEndian(tiBuf.AsSpan(8, 4));
                        int freeBlocks = (int)BinaryPrimitives.ReadUInt32BigEndian(tiBuf.AsSpan(16, 4));
                        int trackSize = (int)BinaryPrimitives.ReadUInt32BigEndian(tiBuf.AsSpan(24, 4));

                        if (isBlankTrack)
                        {
                            freeSectors += Math.Max(0, freeBlocks);
                            continue;
                        }

                        int blockSize = isAudio ? AudioSectorSize : DataSectorSize;
                        long sizeBytes = (long)trackSize * blockSize;
                        var duration = isAudio ? TimeSpan.FromSeconds(trackSize / 75.0) : TimeSpan.Zero;

                        if (!tracksBySession.TryGetValue(sessionNum, out var list))
                        {
                            list = [];
                            tracksBySession[sessionNum] = list;
                        }
                        list.Add(new TrackModel(t, isAudio, trackSize, blockSize, sizeBytes, duration, startLba));
                        usedSectors += Math.Max(0, trackSize);
                        if (freeBlocks > 0) freeSectors += freeBlocks;
                    }
                }

                foreach (var kv in tracksBySession)
                {
                    sessions.Add(new SessionModel(kv.Key, kv.Value));
                }

                // If Read Capacity reports a larger total (e.g. DVD-ROM / CD-ROM), use it
                if (usedSectors == 0 && !isBlank)
                {
                    var capBuf = new byte[8];
                    byte[] capCdb = [0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0]; // READ CAPACITY (10)
                    if (SendScsiCommand(handle, capCdb, capBuf, SCSI_IOCTL_DATA_IN, 5, out _, out _, out _))
                    {
                        long lastLba = BinaryPrimitives.ReadUInt32BigEndian(capBuf.AsSpan(0, 4));
                        if (lastLba > 0 && lastLba < 0x10000000)
                        {
                            usedSectors = lastLba + 1;
                        }
                    }
                }
            }
        }
        catch { }

        long usedBytes = sessions.Count > 0 ? sessions.Sum(s => s.TotalSizeBytes) : usedSectors * DataSectorSize;
        long freeBytes = freeSectors * DataSectorSize;
        long totalBytes = (usedSectors + freeSectors) * DataSectorSize;
        if (totalBytes < usedBytes) totalBytes = usedBytes;

        return new RawDiscLayout(isBlank, isErasable, isFinalized, sessions, usedBytes, freeBytes, totalBytes);
    }

    public static int QueryMaxReadSpeedKBps(string driveLetter)
    {
        int best = 0;
        try
        {
            using var handle = OpenDrive(driveLetter, writeAccess: true);
            if (handle.IsInvalid) return 0;

            // 1. GET PERFORMANCE (0xAC), DataType = 0x10 (Nominal Read Performance)
            var perfBuf = new byte[8 + 16 * 16];
            byte[] perfCdb = [0xAC, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 16, 0x00, 0x00];
            if (SendScsiCommand(handle, perfCdb, perfBuf, SCSI_IOCTL_DATA_IN, 10, out _, out _, out _))
            {
                int dataLen = Math.Min(perfBuf.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(perfBuf.AsSpan(0, 4)) + 4);
                for (int p = 8; p + 16 <= dataLen; p += 16)
                {
                    int endPerf = (int)BinaryPrimitives.ReadUInt32BigEndian(perfBuf.AsSpan(p + 12, 4));
                    if (endPerf > best && endPerf < 200_000) best = endPerf;
                }
            }

            // 2. MODE SENSE (10) — Page 0x2A fallback
            var modeBuf = new byte[128];
            byte[] modeCdb = [0x5A, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)modeBuf.Length, 0x00];
            if (SendScsiCommand(handle, modeCdb, modeBuf, SCSI_IOCTL_DATA_IN, 10, out _, out _, out _))
            {
                int blockDescLen = BinaryPrimitives.ReadUInt16BigEndian(modeBuf.AsSpan(6, 2));
                int pageOffset = 8 + blockDescLen;
                if (pageOffset + 10 <= modeBuf.Length && (modeBuf[pageOffset] & 0x3F) == 0x2A)
                {
                    int maxRead = BinaryPrimitives.ReadUInt16BigEndian(modeBuf.AsSpan(pageOffset + 8, 2));
                    if (maxRead > best) best = maxRead;
                }
            }
        }
        catch { }
        return best;
    }

    public static void SetReadWriteSpeed(SafeFileHandle handle, int readKBps, int writeKBps)
    {
        // 1. SET STREAMING (0xB6): clear prior caps with RDD=1 when Maximum (readKBps <= 0),
        //    or set explicit read rate in KB/s for modern SATA drives that ignore 0xBB.
        var streamDesc = new byte[28];
        if (readKBps <= 0 && writeKBps <= 0)
        {
            streamDesc[0] = 0x04; // RDD (Restore Drive Defaults)
        }
        else
        {
            uint rRate = readKBps <= 0 ? 0x000186A0u : (uint)Math.Clamp(readKBps, 176, 100_000);
            uint wRate = writeKBps <= 0 ? 0x000186A0u : (uint)Math.Clamp(writeKBps, 176, 100_000);
            BinaryPrimitives.WriteUInt32BigEndian(streamDesc.AsSpan(8, 4), 0x003FFFFFu); // End LBA
            BinaryPrimitives.WriteUInt32BigEndian(streamDesc.AsSpan(12, 4), rRate);
            BinaryPrimitives.WriteUInt32BigEndian(streamDesc.AsSpan(16, 4), 1000); // 1000 ms
            BinaryPrimitives.WriteUInt32BigEndian(streamDesc.AsSpan(20, 4), wRate);
            BinaryPrimitives.WriteUInt32BigEndian(streamDesc.AsSpan(24, 4), 1000); // 1000 ms
        }
        byte[] streamCdb = [0xB6, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 28, 0x00];
        bool okB6 = SendScsiCommand(handle, streamCdb, streamDesc, SCSI_IOCTL_DATA_OUT, 10, out byte skB6, out byte ascB6, out byte ascqB6);

        // 2. Legacy SET CD SPEED (0xBB)
        ushort readVal = readKBps <= 0 ? (ushort)0xFFFF : (ushort)Math.Clamp(readKBps, 1, 0xFFFE);
        ushort writeVal = writeKBps <= 0 ? (ushort)0xFFFF : (ushort)Math.Clamp(writeKBps, 1, 0xFFFE);
        byte[] cdb =
        [
            0xBB, 0x00,
            (byte)(readVal >> 8), (byte)(readVal & 0xFF),
            (byte)(writeVal >> 8), (byte)(writeVal & 0xFF),
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];
        bool okBB = SendScsiCommand(handle, cdb, null, SCSI_IOCTL_DATA_UNSPECIFIED, 10, out byte skBB, out byte ascBB, out byte ascqBB);
        DiagnosticLogger.Verbose("SCSI",
            $"SetReadWriteSpeed(read={readKBps}, write={writeKBps}) -> 0xB6:{(okB6 ? "OK" : $"{skB6:X2}/{ascB6:X2}/{ascqB6:X2}")}, 0xBB:{(okBB ? "OK" : $"{skBB:X2}/{ascBB:X2}/{ascqBB:X2}")}");
    }

    public static bool TrySetReadSpeed(string? driveLetter, int readSpeedKBps)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            return false;

        try
        {
            using var handle = OpenDrive(driveLetter, writeAccess: true);
            if (handle.IsInvalid) return false;
            SetReadWriteSpeed(handle, readSpeedKBps, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // =========================================================================
    // Linear Data Sector Reading (Grab ISO / Track) & Post-Burn Verification
    // =========================================================================

    /// <summary>
    /// Converts a user-configured retry count (0..N) into total read attempts (1 initial read + retries).
    /// </summary>
    public static int GetMaxReadAttempts(int retries) => 1 + Math.Max(0, retries);

    /// <summary>
    /// Parses a MODE SENSE (10) response for Mode Page 0x01 (Read/Write Error Recovery Parameters)
    /// and builds both the exact restore buffer and a modified MODE SELECT (10) buffer that disables
    /// hardware error correction / retries according to <paramref name="variant"/>:
    /// 0 = TB (0x20) | DCR (0x01) + ReadRetryCount = 0;
    /// 1 = TB (0x20) + ReadRetryCount = 0;
    /// 2 = ReadRetryCount = 0 only.
    /// </summary>
    internal static byte[]? BuildDisabledEcModeSelectBuffer(
        ReadOnlySpan<byte> modeSense10Buf,
        int variant,
        out byte[]? savedOriginalModeSelect)
    {
        savedOriginalModeSelect = null;
        if (modeSense10Buf.Length < 12)
            return null;

        int modeDataLen = BinaryPrimitives.ReadUInt16BigEndian(modeSense10Buf.Slice(0, 2));
        int validLen = Math.Min(modeSense10Buf.Length, modeDataLen + 2);
        if (validLen < 12)
            return null;

        int blockDescLen = BinaryPrimitives.ReadUInt16BigEndian(modeSense10Buf.Slice(6, 2));
        int pageOff = 8 + blockDescLen;
        if (pageOff + 4 > validLen)
            return null;

        if ((modeSense10Buf[pageOff] & 0x3F) != 0x01)
            return null;

        int pageLen = modeSense10Buf[pageOff + 1] + 2;
        if (pageLen < 4 || pageOff + pageLen > validLen)
            return null;

        int totalSelectLen = 8 + pageLen;
        var originalSelect = new byte[totalSelectLen];
        modeSense10Buf.Slice(pageOff, pageLen).CopyTo(originalSelect.AsSpan(8));
        originalSelect[8] &= 0x3F; // Clear PS (Parameters Saveable) bit for MODE SELECT (10)
        savedOriginalModeSelect = originalSelect;

        var modified = (byte[])originalSelect.Clone();
        byte origFlags = modified[8 + 2];
        // Preserve AWRE (0x80), clear ARRE (0x40) during raw recovery, and apply requested EC flags:
        modified[8 + 2] = variant switch
        {
            0 => (byte)((origFlags & 0x80) | 0x20 | 0x01), // TB | DCR
            1 => (byte)((origFlags & 0x80) | 0x20),        // TB only
            _ => (byte)(origFlags & ~0x40)                 // Keep drive EC flags, clear ARRE
        };
        modified[8 + 3] = 0x00; // Read Retry Count = 0
        return modified;
    }

    private static bool TryDisableHardwareErrorCorrection(SafeFileHandle handle, out byte[]? savedModeSelect)
    {
        savedModeSelect = null;
        var senseBuf = new byte[64];
        byte[] senseCdb = [0x5A, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)senseBuf.Length, 0x00];
        if (!SendScsiCommand(handle, senseCdb, senseBuf, SCSI_IOCTL_DATA_IN, 10, out _, out _, out _))
            return false;

        for (int variant = 0; variant <= 2; variant++)
        {
            byte[]? selectBuf = BuildDisabledEcModeSelectBuffer(senseBuf, variant, out var original);
            if (selectBuf is null || original is null)
                return false;

            int totalLen = selectBuf.Length;
            byte[] selectCdb = [0x55, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)(totalLen >> 8), (byte)(totalLen & 0xFF), 0x00];
            if (SendScsiCommand(handle, selectCdb, selectBuf, SCSI_IOCTL_DATA_OUT, 10, out _, out _, out _))
            {
                savedModeSelect = original;
                return true;
            }
        }

        return false;
    }

    private static void RestoreHardwareErrorCorrection(SafeFileHandle handle, byte[]? savedModeSelect)
    {
        if (savedModeSelect is null || savedModeSelect.Length < 12)
            return;

        try
        {
            int totalLen = savedModeSelect.Length;
            byte[] selectCdb = [0x55, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)(totalLen >> 8), (byte)(totalLen & 0xFF), 0x00];
            SendScsiCommand(handle, selectCdb, savedModeSelect, SCSI_IOCTL_DATA_OUT, 10, out _, out _, out _);
        }
        catch
        {
            // Best-effort restore on close.
        }
    }

    public static IDisposable EnterHardwareErrorCorrectionScope(string? driveLetter, bool disableHardwareEc)
    {
        if (!disableHardwareEc || string.IsNullOrWhiteSpace(driveLetter))
            return NullDisposable.Instance;

        try
        {
            var handle = OpenDrive(driveLetter, writeAccess: true);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return NullDisposable.Instance;
            }

            if (!TryDisableHardwareErrorCorrection(handle, out var savedPage01))
            {
                handle.Dispose();
                return NullDisposable.Instance;
            }

            return new HardwareEcScope(handle, savedPage01);
        }
        catch
        {
            return NullDisposable.Instance;
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class HardwareEcScope(SafeFileHandle handle, byte[]? savedPage01) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                RestoreHardwareErrorCorrection(handle, savedPage01);
            }
            finally
            {
                handle.Dispose();
            }
        }
    }

    public static BadSectorTracker GrabDataSectorsToFile(
        string driveLetter,
        string outputPath,
        long startLba,
        long sectorCount,
        int readSpeedKBps,
        int retries,
        bool ignoreBadBlocks,
        IProgress<BurnProgress>? progress,
        CancellationToken ct,
        bool disableHardwareEc = false)
    {
        using var handle = OpenDrive(driveLetter, writeAccess: true);
        if (handle.IsInvalid)
            throw new IOException($"Could not open optical drive {driveLetter}: for reading.");

        SetReadWriteSpeed(handle, readSpeedKBps, 0);

        var capBuf = new byte[8];
        byte[] capCdb = [0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        if (SendScsiCommand(handle, capCdb, capBuf, SCSI_IOCTL_DATA_IN, 10, out _, out _, out _))
        {
            long capacitySectors = BinaryPrimitives.ReadUInt32BigEndian(capBuf.AsSpan(0, 4)) + 1L - startLba;
            if (capacitySectors > 0)
            {
                if (sectorCount <= 0 || capacitySectors < sectorCount)
                    sectorCount = capacitySectors;
            }
        }
        if (sectorCount <= 0)
            throw new InvalidOperationException("Unable to determine disc sector count.");

        byte[]? savedPage01 = null;
        if (disableHardwareEc)
            TryDisableHardwareErrorCorrection(handle, out savedPage01);

        const int chunkSectors = 32; // 64 KB per SCSI READ(10)
        int singleSectorAttempts = GetMaxReadAttempts(retries);
        var buffer = new byte[chunkSectors * DataSectorSize];
        var singleSector = new byte[DataSectorSize];
        var tracker = new BadSectorTracker();
        var sw = Stopwatch.StartNew();

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        bool completed = false;
        try
        {
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
            long readSoFar = 0;
            int lastPct = -1;
            int lastReportedErrors = 0;
            long lastFallbackReportMs = 0;
            bool reachedEndOfDisc = false;

            while (readSoFar < sectorCount && !reachedEndOfDisc)
            {
                ct.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(chunkSectors, sectorCount - readSoFar);
                long lba = startLba + readSoFar;

                // Try 1 fast multi-sector read; if any sector in the chunk fails, step through
                // individual sectors using the exact configured retry count (1 + retries).
                bool ok = TryReadSectors10(handle, (uint)lba, (ushort)toRead, buffer, maxAttempts: 1, ct, out _);
                if (ok)
                {
                    output.Write(buffer, 0, toRead * DataSectorSize);
                    readSoFar += toRead;
                }
                else
                {
                    for (int s = 0; s < toRead; s++)
                    {
                        ct.ThrowIfCancellationRequested();
                        long sectorLba = lba + s;
                        if (!TryReadSectors10(handle, (uint)sectorLba, 1, singleSector, singleSectorAttempts, ct, out bool outOfRange))
                        {
                            if (outOfRange && readSoFar > 0)
                            {
                                // Drive reported Illegal Request / LBA Out of Range after reading valid data:
                                // we have reached the actual readable end of the disc.
                                reachedEndOfDisc = true;
                                break;
                            }

                            if (!ignoreBadBlocks)
                                throw new IOException($"Unreadable sector at LBA {sectorLba} after {singleSectorAttempts} attempt(s).");
                            Array.Clear(singleSector, 0, singleSector.Length);
                            tracker.RecordBadSector(sectorLba);
                        }
                        output.Write(singleSector, 0, DataSectorSize);
                        readSoFar++;

                        long nowMs = sw.ElapsedMilliseconds;
                        if (nowMs - lastFallbackReportMs >= 500 || tracker.BadSectorCount != lastReportedErrors)
                        {
                            lastFallbackReportMs = nowMs;
                            lastReportedErrors = tracker.BadSectorCount;
                            int curPct = (int)Math.Clamp(readSoFar * 100L / sectorCount, 0, 100);
                            lastPct = curPct;
                            string errSuffix = tracker.BadSectorCount > 0
                                ? $" (Read errors: {tracker.BadSectorCount})"
                                : string.Empty;
                            progress?.Report(new BurnProgress(
                                BurnPhase.Grabbing,
                                curPct,
                                Message: $"Reading disc... {curPct}% (sector {sectorLba:N0}){errSuffix}",
                                ReadErrors: tracker.BadSectorCount));
                        }
                    }
                }

                int pct = (int)Math.Clamp(readSoFar * 100L / sectorCount, 0, 100);
                if (pct != lastPct || tracker.BadSectorCount != lastReportedErrors)
                {
                    lastPct = pct;
                    lastReportedErrors = tracker.BadSectorCount;
                    TimeSpan eta = readSoFar > 0 && sw.Elapsed.TotalSeconds > 0.2
                        ? TimeSpan.FromSeconds((sectorCount - readSoFar) / (readSoFar / sw.Elapsed.TotalSeconds))
                        : TimeSpan.Zero;
                    string errSuffix = tracker.BadSectorCount > 0
                        ? $" (Read errors: {tracker.BadSectorCount})"
                        : string.Empty;
                    progress?.Report(new BurnProgress(
                        BurnPhase.Grabbing,
                        pct,
                        TimeRemaining: eta,
                        Message: $"Reading disc... {pct}%{errSuffix}",
                        ReadErrors: tracker.BadSectorCount));
                }
            }

            completed = true;
            return tracker;
        }
        finally
        {
            RestoreHardwareErrorCorrection(handle, savedPage01);
            if (!completed)
            {
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            }
        }
    }

    public static void VerifyStreamAgainstDisc(
        string driveLetter,
        IStream expectedStream,
        long startLba,
        long totalBytes,
        IProgress<BurnProgress>? progress,
        CancellationToken ct)
    {
        using var handle = OpenDrive(driveLetter, writeAccess: true);
        if (handle.IsInvalid)
            throw new IOException($"Could not open drive {driveLetter}: for verification.");

        long totalSectors = (totalBytes + DataSectorSize - 1) / DataSectorSize;
        if (totalSectors <= 0) return;

        const int chunkSectors = 32;
        var discBuf = new byte[chunkSectors * DataSectorSize];
        var expectedBuf = new byte[chunkSectors * DataSectorSize];
        IntPtr readPtr = Marshal.AllocHGlobal(sizeof(int));
        var sw = Stopwatch.StartNew();

        try
        {
            expectedStream.Seek(0, 0, IntPtr.Zero);
            long verifiedSectors = 0;
            int lastPct = -1;

            while (verifiedSectors < totalSectors)
            {
                ct.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(chunkSectors, totalSectors - verifiedSectors);
                int byteLen = toRead * DataSectorSize;

                expectedStream.Read(expectedBuf, byteLen, readPtr);
                int expRead = Marshal.ReadInt32(readPtr);
                if (expRead <= 0) break;

                if (!TryReadSectors10(handle, (uint)(startLba + verifiedSectors), (ushort)toRead, discBuf, 3))
                    throw new IOException($"Verification failed: unable to read sector {startLba + verifiedSectors} from disc.");

                if (!discBuf.AsSpan(0, expRead).SequenceEqual(expectedBuf.AsSpan(0, expRead)))
                    throw new IOException($"Verification mismatch detected around sector {startLba + verifiedSectors}.");

                verifiedSectors += toRead;
                int pct = (int)Math.Clamp(verifiedSectors * 100L / totalSectors, 0, 100);
                if (pct != lastPct)
                {
                    lastPct = pct;
                    TimeSpan eta = verifiedSectors > 0 && sw.Elapsed.TotalSeconds > 0.2
                        ? TimeSpan.FromSeconds((totalSectors - verifiedSectors) / (verifiedSectors / sw.Elapsed.TotalSeconds))
                        : TimeSpan.Zero;
                    progress?.Report(new BurnProgress(BurnPhase.Verifying, pct, 0, eta, Message: $"Verifying... {pct}%"));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(readPtr);
        }
    }

    private static bool TryReadSectors10(SafeFileHandle handle, uint lba, ushort count, byte[] buffer, int maxAttempts) =>
        TryReadSectors10(handle, lba, count, buffer, maxAttempts, CancellationToken.None, out _);

    private static bool TryReadSectors10(
        SafeFileHandle handle,
        uint lba,
        ushort count,
        byte[] buffer,
        int maxAttempts,
        CancellationToken ct,
        out bool lbaOutOfRange)
    {
        lbaOutOfRange = false;
        byte[] cdb =
        [
            0x28, 0x00,
            (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)(lba & 0xFF),
            0x00,
            (byte)(count >> 8), (byte)(count & 0xFF),
            0x00
        ];

        int attempts = Math.Max(1, maxAttempts);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (SendScsiCommand(handle, cdb, buffer, SCSI_IOCTL_DATA_IN, 30, out byte sk, out byte asc, out _))
                return true;

            // Sense Key 0x05 (Illegal Request) + ASC 0x21 (Logical Block Address Out of Range):
            // Retrying will never succeed because the LBA is past the end of the medium.
            if (sk == 0x05 && asc == 0x21)
            {
                lbaOutOfRange = true;
                return false;
            }

            if (attempt + 1 < attempts)
                Thread.Sleep(25);
        }
        return false;
    }

    // =========================================================================
    // Pure-C# MMC-3/5 Audio CD SAO/DAO-96 Burning with CD-Text & Custom Gaps
    // =========================================================================

    public sealed record DaoAudioTrack(
        string WavPath,
        string Title,
        string Artist,
        int PreGapSectors);

    public static void BurnAudioDao(
        string driveLetter,
        IReadOnlyList<DaoAudioTrack> tracks,
        string discTitle,
        string discArtist,
        bool enableCdText,
        bool testBurn,
        int writeSpeedKBps,
        IProgress<BurnProgress>? progress,
        CancellationToken ct)
    {
        if (tracks.Count == 0)
            throw new InvalidOperationException("No audio tracks provided for DAO burn.");

        // Read WAV header offsets and PCM sector counts
        var trackStreams = new List<(string Path, long DataOffset, long DataLength, int Sectors, int PreGap)>();
        long totalAudioSectors = 0;
        foreach (var t in tracks)
        {
            var (offset, length) = ReadWavPcmBounds(t.WavPath);
            int sectors = (int)((length + AudioSectorSize - 1) / AudioSectorSize);
            if (sectors < 300) sectors = 300; // Red Book minimum 4 seconds per track
            int preGap = Math.Max(0, t.PreGapSectors);
            trackStreams.Add((t.WavPath, offset, length, sectors, preGap));
            totalAudioSectors += sectors + preGap;
        }

        using var handle = OpenDrive(driveLetter, writeAccess: true);
        if (handle.IsInvalid)
            throw new IOException($"Could not open drive {driveLetter}: for Audio DAO burning.");

        SetTrayLock(driveLetter, true);
        try
        {
            if (writeSpeedKBps > 0)
                SetReadWriteSpeed(handle, 0, writeSpeedKBps);

            // 1. Configure Mode Page 0x05 (Write Parameters) for SAO (WriteType = 0x02)
            ConfigureSaoWriteParameters(handle, testBurn);

            // 2. Build MMC Cue Sheet
            byte[] cueSheet = BuildSaoCueSheet(trackStreams);

            // 3. SEND CUE SHEET (0x5D)
            byte[] cueCdb =
            [
                0x5D, 0x00, 0x00, 0x00, 0x00, 0x00,
                (byte)((cueSheet.Length >> 16) & 0xFF),
                (byte)((cueSheet.Length >> 8) & 0xFF),
                (byte)(cueSheet.Length & 0xFF),
                0x00
            ];
            if (!SendScsiCommand(handle, cueCdb, cueSheet, SCSI_IOCTL_DATA_OUT, 60, out byte sk, out byte asc, out byte ascq))
            {
                throw new IOException($"Drive rejected SAO Cue Sheet (Sense {sk:X2}/{asc:X2}/{ascq:X2}).");
            }

            // 4. Stream PCM sectors starting at LBA -150 (lead-in/pregap of track 1)
            const int chunkSectors = 26; // ~61 KB per WRITE(10)
            var writeBuf = new byte[chunkSectors * AudioSectorSize];
            var silenceSector = new byte[AudioSectorSize];
            int currentLba = -150;
            long writtenSectors = 0;
            var sw = Stopwatch.StartNew();

            // Write 150-sector mandatory track 1 pre-gap silence
            WriteSilenceSectors(handle, ref currentLba, 150, silenceSector, writeBuf, ct);

            for (int i = 0; i < trackStreams.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ts = trackStreams[i];

                // Inter-track pre-gap (Track 2+)
                if (i > 0 && ts.PreGap > 0)
                {
                    WriteSilenceSectors(handle, ref currentLba, ts.PreGap, silenceSector, writeBuf, ct);
                    writtenSectors += ts.PreGap;
                }

                using var fs = new FileStream(ts.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(ts.DataOffset, SeekOrigin.Begin);

                int remainingTrackSectors = ts.Sectors;
                long remainingPcmBytes = ts.DataLength;

                while (remainingTrackSectors > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int batch = Math.Min(chunkSectors, remainingTrackSectors);
                    int batchBytes = batch * AudioSectorSize;
                    Array.Clear(writeBuf, 0, batchBytes);

                    int toReadFromFile = (int)Math.Min(batchBytes, remainingPcmBytes);
                    int filled = 0;
                    while (filled < toReadFromFile)
                    {
                        int r = fs.Read(writeBuf, filled, toReadFromFile - filled);
                        if (r <= 0) break;
                        filled += r;
                    }
                    remainingPcmBytes -= filled;

                    WriteAudioSectors10(handle, currentLba, batch, writeBuf);
                    currentLba += batch;
                    remainingTrackSectors -= batch;
                    writtenSectors += batch;

                    int pct = (int)Math.Clamp(writtenSectors * 100L / Math.Max(1, totalAudioSectors), 0, 100);
                    TimeSpan eta = writtenSectors > 0 && sw.Elapsed.TotalSeconds > 0.5
                        ? TimeSpan.FromSeconds((totalAudioSectors - writtenSectors) / (writtenSectors / sw.Elapsed.TotalSeconds))
                        : TimeSpan.Zero;

                    progress?.Report(new BurnProgress(
                        BurnPhase.Burning,
                        pct,
                        100,
                        eta,
                        i + 1,
                        trackStreams.Count,
                        $"Burning audio track {i + 1}/{trackStreams.Count} (DAO)... {pct}%"));
                }
            }

            // 5. SYNCHRONIZE CACHE (0x35) to finalize Lead-Out
            progress?.Report(new BurnProgress(BurnPhase.ClosingSession, 99, 100, Message: "Closing audio disc (Lead-Out)..."));
            byte[] syncCdb = [0x35, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
            SendScsiCommand(handle, syncCdb, null, SCSI_IOCTL_DATA_UNSPECIFIED, 180, out _, out _, out _);
        }
        finally
        {
            SetTrayLock(driveLetter, false);
        }
    }

    private static void ConfigureSaoWriteParameters(SafeFileHandle handle, bool testBurn)
    {
        // Read existing Mode Page 0x05 first
        var buf = new byte[64];
        byte[] senseCdb = [0x5A, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)buf.Length, 0x00];
        if (!SendScsiCommand(handle, senseCdb, buf, SCSI_IOCTL_DATA_IN, 10, out _, out _, out _))
            throw new IOException("Drive does not support MMC Write Parameters mode page (0x05).");

        int blockDescLen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6, 2));
        int pageOff = 8 + blockDescLen;
        int pageLen = buf[pageOff + 1] + 2;
        int totalLen = 8 + pageLen;

        var modeSelectBuf = new byte[totalLen];
        Array.Copy(buf, pageOff, modeSelectBuf, 8, pageLen);
        modeSelectBuf[8] &= 0x3F; // Clear PS bit
        // Byte +2: BUFE (0x40) | TestWrite (0x10) | WriteType = 0x02 (SAO)
        modeSelectBuf[8 + 2] = (byte)(0x40 | (testBurn ? 0x10 : 0x00) | 0x02);
        // Byte +3: Multi-Session = 0 (closed) | TrackMode = 0 (2-ch audio without pre-emphasis)
        modeSelectBuf[8 + 3] = 0x00;
        // Byte +4: DataBlockType = 0 (Raw 2352-byte audio)
        modeSelectBuf[8 + 4] = 0x00;
        // Byte +8: SessionFormat = 0x00 (CD-DA)
        modeSelectBuf[8 + 8] = 0x00;

        byte[] selectCdb = [0x55, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)(totalLen >> 8), (byte)(totalLen & 0xFF), 0x00];
        if (!SendScsiCommand(handle, selectCdb, modeSelectBuf, SCSI_IOCTL_DATA_OUT, 15, out byte sk, out byte asc, out byte ascq))
            throw new IOException($"Failed to set SAO write parameters (Sense {sk:X2}/{asc:X2}/{ascq:X2}).");
    }

    private static byte[] BuildSaoCueSheet(
        List<(string Path, long DataOffset, long DataLength, int Sectors, int PreGap)> tracks)
    {
        var entries = new List<byte>(8 * (tracks.Count * 2 + 2));

        // 1. Lead-in entry (TNO=0x00, Index=0x00, DataForm = 0x01 audio lead-in)
        entries.AddRange(MakeCueEntry(0x01, 0x00, 0x00, 0x01, 0x00, 0));

        int absSector = 0; // Track 1 Index 0 starts at 00:00:00 (00:02:00 after 150-sector pregap)
        for (int i = 0; i < tracks.Count; i++)
        {
            byte tno = (byte)(i + 1);
            int preGap = i == 0 ? 150 : tracks[i].PreGap;

            if (i == 0 || preGap > 0)
            {
                // Index 00 (Pre-gap)
                entries.AddRange(MakeCueEntry(0x01, tno, 0x00, 0x00, 0x00, absSector));
                absSector += preGap;
            }

            // Index 01 (Track audio start)
            entries.AddRange(MakeCueEntry(0x01, tno, 0x01, 0x00, 0x00, absSector));
            absSector += tracks[i].Sectors;
        }

        // Lead-out entry (TNO=0xAA, Index=0x01, DataForm = 0x01)
        entries.AddRange(MakeCueEntry(0x01, 0xAA, 0x01, 0x01, 0x00, absSector));
        return entries.ToArray();
    }

    private static byte[] MakeCueEntry(byte ctlAdr, byte tno, byte index, byte dataForm, byte scms, int absSectors)
    {
        int totalFrames = absSectors;
        byte frames = (byte)(totalFrames % 75);
        int totalSec = totalFrames / 75;
        byte sec = (byte)(totalSec % 60);
        byte min = (byte)(totalSec / 60);
        return [ctlAdr, tno, index, dataForm, scms, min, sec, frames];
    }

    private static byte[] BuildCdTextPacks(string discTitle, string discArtist, IReadOnlyList<DaoAudioTrack> tracks)
    {
        var packs = new List<byte>();
        AppendCdTextBlock(packs, 0x80, discTitle, tracks.Select(t => t.Title));   // Album & Track Titles
        AppendCdTextBlock(packs, 0x81, discArtist, tracks.Select(t => t.Artist)); // Album & Track Performers
        return packs.ToArray();
    }

    private static void AppendCdTextBlock(List<byte> output, byte packType, string discText, IEnumerable<string> trackTexts)
    {
        var items = new List<string> { discText ?? string.Empty };
        items.AddRange(trackTexts.Select(t => t ?? string.Empty));

        byte seq = (byte)(output.Count / 18);
        var payload = new byte[12];
        int pIdx = 0;
        byte currentTrack = 0;
        byte charPos = 0;

        for (int t = 0; t < items.Count; t++)
        {
            byte[] ascii = Encoding.Latin1.GetBytes(items[t]);
            for (int c = 0; c <= ascii.Length; c++)
            {
                if (pIdx == 0)
                {
                    currentTrack = (byte)t;
                    charPos = (byte)Math.Min(15, c);
                }

                payload[pIdx++] = c < ascii.Length ? ascii[c] : (byte)0;
                if (pIdx == 12)
                {
                    EmitCdTextPack(output, packType, currentTrack, seq++, charPos, payload);
                    Array.Clear(payload, 0, 12);
                    pIdx = 0;
                }
            }
        }

        if (pIdx > 0)
        {
            EmitCdTextPack(output, packType, currentTrack, seq++, charPos, payload);
        }
    }

    private static void EmitCdTextPack(List<byte> output, byte id1, byte id2, byte id3, byte id4, byte[] payload12)
    {
        var pack = new byte[18];
        pack[0] = id1;
        pack[1] = id2;
        pack[2] = id3;
        pack[3] = id4;
        Array.Copy(payload12, 0, pack, 4, 12);
        ushort crc = ComputeCrc16Ccitt(pack, 16);
        pack[16] = (byte)(~(crc >> 8) & 0xFF);
        pack[17] = (byte)(~crc & 0xFF);
        output.AddRange(pack);
    }

    private static ushort ComputeCrc16Ccitt(byte[] data, int length)
    {
        ushort crc = 0;
        for (int i = 0; i < length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int b = 0; b < 8; b++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    private static void WriteSilenceSectors(SafeFileHandle handle, ref int currentLba, int count, byte[] silenceSector, byte[] buf, CancellationToken ct)
    {
        int maxChunk = buf.Length / AudioSectorSize;
        Array.Clear(buf, 0, buf.Length);
        int rem = count;
        while (rem > 0)
        {
            ct.ThrowIfCancellationRequested();
            int batch = Math.Min(maxChunk, rem);
            WriteAudioSectors10(handle, currentLba, batch, buf);
            currentLba += batch;
            rem -= batch;
        }
    }

    private static void WriteAudioSectors10(SafeFileHandle handle, int lba, int sectorCount, byte[] buffer)
    {
        uint ulba = unchecked((uint)lba);
        byte[] cdb =
        [
            0x2A, 0x00,
            (byte)(ulba >> 24), (byte)(ulba >> 16), (byte)(ulba >> 8), (byte)(ulba & 0xFF),
            0x00,
            (byte)(sectorCount >> 8), (byte)(sectorCount & 0xFF),
            0x00
        ];

        byte[] tx = buffer.Length == sectorCount * AudioSectorSize
            ? buffer
            : buffer.AsSpan(0, sectorCount * AudioSectorSize).ToArray();

        if (!SendScsiCommand(handle, cdb, tx, SCSI_IOCTL_DATA_OUT, 60, out byte sk, out byte asc, out byte ascq))
            throw new IOException($"SCSI WRITE(10) failed at LBA {lba} (Sense {sk:X2}/{asc:X2}/{ascq:X2}).");
    }

    public static (long DataOffset, long DataLength) ReadWavPcmBounds(string wavPath)
    {
        using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);
        if (fs.Length < 44) return (0, fs.Length);

        var riff = br.ReadBytes(4);
        if (Encoding.ASCII.GetString(riff) != "RIFF") return (0, fs.Length);
        br.ReadInt32(); // file size
        var wave = br.ReadBytes(4);
        if (Encoding.ASCII.GetString(wave) != "WAVE") return (0, fs.Length);

        while (fs.Position + 8 <= fs.Length)
        {
            string chunkId = Encoding.ASCII.GetString(br.ReadBytes(4));
            uint chunkSize = br.ReadUInt32();
            if (chunkId == "data")
            {
                return (fs.Position, Math.Min(chunkSize, fs.Length - fs.Position));
            }
            long next = fs.Position + chunkSize + (chunkSize & 1);
            if (next > fs.Length) break;
            fs.Seek(next, SeekOrigin.Begin);
        }

        return (44, Math.Max(0, fs.Length - 44));
    }

    // =========================================================================
    // Pure-C# MMC-3/5 Audio CD Track-at-Once (TAO) — Supports Multi-Session CD-R!
    // =========================================================================

    public static void BurnAudioTao(
        string driveLetter,
        IReadOnlyList<string> wavPaths,
        bool finalizeDisc,
        bool testBurn,
        int writeSpeedKBps,
        IProgress<BurnProgress>? progress,
        CancellationToken ct)
    {
        if (wavPaths.Count == 0)
            throw new InvalidOperationException("No audio tracks provided for TAO burn.");

        var trackBounds = new List<(string Path, long DataOffset, long DataLength, int Sectors)>(wavPaths.Count);
        long totalSectors = 0;
        foreach (var path in wavPaths)
        {
            var (offset, length) = ReadWavPcmBounds(path);
            int sectors = (int)((length + AudioSectorSize - 1) / AudioSectorSize);
            if (sectors < 300) sectors = 300; // Red Book minimum 4 seconds per track
            trackBounds.Add((path, offset, length, sectors));
            totalSectors += sectors;
        }

        using var handle = OpenDrive(driveLetter, writeAccess: true);
        if (handle.IsInvalid)
            throw new IOException($"Could not open drive {driveLetter}: for Audio TAO burning.");

        SetTrayLock(driveLetter, true);
        try
        {
            if (writeSpeedKBps > 0)
                SetReadWriteSpeed(handle, 0, writeSpeedKBps);

            // 1. Configure Mode Page 0x05 (Write Parameters) for TAO (WriteType = 0x01)
            ConfigureTaoWriteParameters(handle, finalizeDisc, testBurn);

            const int chunkSectors = 26;
            var writeBuf = new byte[chunkSectors * AudioSectorSize];
            long writtenSectors = 0;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < trackBounds.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var tb = trackBounds[i];

                // Query Track Number and Next Writable Address (NWA) for the invisible/open track (0xFF)
                var (trackNumber, currentLba) = QueryInvisibleTrackInfo(handle);

                using var fs = new FileStream(tb.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(tb.DataOffset, SeekOrigin.Begin);

                int remainingTrackSectors = tb.Sectors;
                long remainingPcmBytes = tb.DataLength;

                while (remainingTrackSectors > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int batch = Math.Min(chunkSectors, remainingTrackSectors);
                    int batchBytes = batch * AudioSectorSize;
                    Array.Clear(writeBuf, 0, batchBytes);

                    int toReadFromFile = (int)Math.Min(batchBytes, remainingPcmBytes);
                    int filled = 0;
                    while (filled < toReadFromFile)
                    {
                        int r = fs.Read(writeBuf, filled, toReadFromFile - filled);
                        if (r <= 0) break;
                        filled += r;
                    }
                    remainingPcmBytes -= filled;

                    WriteAudioSectors10(handle, currentLba, batch, writeBuf);
                    currentLba += batch;
                    remainingTrackSectors -= batch;
                    writtenSectors += batch;

                    int pct = (int)Math.Clamp(writtenSectors * 100L / Math.Max(1, totalSectors), 0, 100);
                    TimeSpan eta = writtenSectors > 0 && sw.Elapsed.TotalSeconds > 0.5
                        ? TimeSpan.FromSeconds((totalSectors - writtenSectors) / (writtenSectors / sw.Elapsed.TotalSeconds))
                        : TimeSpan.Zero;

                    progress?.Report(new BurnProgress(
                        BurnPhase.Burning,
                        pct,
                        100,
                        eta,
                        i + 1,
                        trackBounds.Count,
                        $"Burning audio track {i + 1}/{trackBounds.Count} (TAO)... {pct}%"));
                }

                // Flush drive cache and close track
                byte[] syncCdb = [0x35, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
                SendScsiCommand(handle, syncCdb, null, SCSI_IOCTL_DATA_UNSPECIFIED, 120, out _, out _, out _);

                if (!testBurn)
                {
                    // CLOSE TRACK/SESSION (0x5B), CloseFunction = 0x01 (Close Track), actual Track Number (0xFF is invalid in 0x5B)
                    byte[] closeTrackCdb =
                    [
                        0x5B, 0x00, 0x01, 0x00,
                        (byte)((trackNumber >> 8) & 0xFF), (byte)(trackNumber & 0xFF),
                        0x00, 0x00, 0x00, 0x00
                    ];
                    if (!SendScsiCommand(handle, closeTrackCdb, null, SCSI_IOCTL_DATA_UNSPECIFIED, 120, out byte ctSk, out byte ctAsc, out byte ctAscq))
                        throw new IOException($"Failed to close audio track {trackNumber} (Sense {ctSk:X2}/{ctAsc:X2}/{ctAscq:X2}).");
                    WaitForDriveReady(handle, ct);
                }
            }

            if (!testBurn)
            {
                progress?.Report(new BurnProgress(BurnPhase.ClosingSession, 99, 100, Message: "Closing audio session..."));
                // CLOSE TRACK/SESSION (0x5B), CloseFunction = 0x02 (Close Session)
                byte[] closeSessionCdb = [0x5B, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
                if (!SendScsiCommand(handle, closeSessionCdb, null, SCSI_IOCTL_DATA_UNSPECIFIED, 180, out byte csSk, out byte csAsc, out byte csAscq))
                    throw new IOException($"Failed to close audio session (Sense {csSk:X2}/{csAsc:X2}/{csAscq:X2}).");
                WaitForDriveReady(handle, ct);
            }
        }
        finally
        {
            SetTrayLock(driveLetter, false);
        }
    }

    private static void ConfigureTaoWriteParameters(SafeFileHandle handle, bool finalizeDisc, bool testBurn)
    {
        var buf = new byte[64];
        byte[] senseCdb = [0x5A, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)buf.Length, 0x00];
        if (!SendScsiCommand(handle, senseCdb, buf, SCSI_IOCTL_DATA_IN, 10, out _, out _, out _))
            throw new IOException("Drive does not support MMC Write Parameters mode page (0x05).");

        int blockDescLen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6, 2));
        int pageOff = 8 + blockDescLen;
        int pageLen = buf[pageOff + 1] + 2;
        int totalLen = 8 + pageLen;

        var modeSelectBuf = new byte[totalLen];
        Array.Copy(buf, pageOff, modeSelectBuf, 8, pageLen);
        modeSelectBuf[8] &= 0x3F; // Clear PS bit
        // Byte +2: BUFE (0x40) | TestWrite (0x10) | WriteType = 0x01 (TAO)
        modeSelectBuf[8 + 2] = (byte)(0x40 | (testBurn ? 0x10 : 0x00) | 0x01);
        // Byte +3: Multi-Session (bits 7..6): 0xC0 = next session allowed, 0x00 = finalized | TrackMode = 0x00 (2-ch audio)
        modeSelectBuf[8 + 3] = (byte)(finalizeDisc ? 0x00 : 0xC0);
        // Byte +4: DataBlockType = 0x00 (Raw 2352-byte audio)
        modeSelectBuf[8 + 4] = 0x00;
        // Byte +8: SessionFormat = 0x00 (CD-DA)
        modeSelectBuf[8 + 8] = 0x00;

        byte[] selectCdb = [0x55, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)(totalLen >> 8), (byte)(totalLen & 0xFF), 0x00];
        if (!SendScsiCommand(handle, selectCdb, modeSelectBuf, SCSI_IOCTL_DATA_OUT, 15, out byte sk, out byte asc, out byte ascq))
            throw new IOException($"Failed to set TAO write parameters (Sense {sk:X2}/{asc:X2}/{ascq:X2}).");
    }

    private static (int TrackNumber, int NextWritableAddress) QueryInvisibleTrackInfo(SafeFileHandle handle)
    {
        var tiBuf = new byte[36];
        // 1. Try READ TRACK INFORMATION (0x52), Address/Number Type = 1, Track = 0xFF (invisible/open track)
        byte[] tiCdb = [0x52, 0x01, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, (byte)tiBuf.Length, 0x00];
        if (SendScsiCommand(handle, tiCdb, tiBuf, SCSI_IOCTL_DATA_IN, 10, out _, out _, out _))
        {
            int trackNum = tiBuf[2] | (tiBuf[32] << 8);
            bool nwaValid = (tiBuf[7] & 0x01) != 0;
            int nwa = nwaValid
                ? (int)BinaryPrimitives.ReadUInt32BigEndian(tiBuf.AsSpan(12, 4))
                : (int)BinaryPrimitives.ReadUInt32BigEndian(tiBuf.AsSpan(8, 4));
            return (trackNum, nwa);
        }

        // 2. Fallback: query READ DISC INFORMATION (0x51) for lastTrackLastSession and read its Track Information
        var discBuf = new byte[34];
        byte[] discCdb = [0x51, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)discBuf.Length, 0x00];
        if (SendScsiCommand(handle, discCdb, discBuf, SCSI_IOCTL_DATA_IN, 10, out _, out _, out _))
        {
            int lastTrack = discBuf[6] | (discBuf[11] << 8);
            if (lastTrack > 0)
            {
                byte[] tiLastCdb = [0x52, 0x01, 0x00, 0x00, (byte)(lastTrack >> 8), (byte)(lastTrack & 0xFF), 0x00, 0x00, (byte)tiBuf.Length, 0x00];
                if (SendScsiCommand(handle, tiLastCdb, tiBuf, SCSI_IOCTL_DATA_IN, 10, out _, out _, out _))
                {
                    int trackNum = tiBuf[2] | (tiBuf[32] << 8);
                    bool nwaValid = (tiBuf[7] & 0x01) != 0;
                    int nwa = nwaValid
                        ? (int)BinaryPrimitives.ReadUInt32BigEndian(tiBuf.AsSpan(12, 4))
                        : (int)BinaryPrimitives.ReadUInt32BigEndian(tiBuf.AsSpan(8, 4));
                    return (trackNum > 0 ? trackNum : lastTrack, nwa);
                }
            }
        }

        throw new IOException("Unable to query Next Writable Address on disc.");
    }

    private static void WaitForDriveReady(SafeFileHandle handle, CancellationToken ct)
    {
        byte[] turCdb = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        for (int attempt = 0; attempt < 300; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (SendScsiCommand(handle, turCdb, null, SCSI_IOCTL_DATA_UNSPECIFIED, 10, out byte sk, out byte asc, out _))
                return;
            // Sense 02/04 = Logical Unit Not Ready, Operation in Progress
            if (sk != 0x02 && sk != 0x06)
                return;
            Thread.Sleep(500);
        }
    }
}
