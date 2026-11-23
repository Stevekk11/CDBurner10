using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CDBurner10.Core.Audio;

/// <summary>
/// Pure Win32 optical CD-DA audio reader and CD-Text parser using
/// IOCTL_CDROM_READ_TOC_EX and IOCTL_CDROM_RAW_READ (replaces basscd.dll).
/// </summary>
public static class CdAudioDevice
{
    public const int CdAudioSectorBytes = 2352;
    private const int SectorsPerRead = 26;

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private const uint IOCTL_CDROM_READ_TOC_EX = 0x00024054;
    private const uint IOCTL_CDROM_RAW_READ = 0x0002403E;

    private const byte CDROM_READ_TOC_EX_FORMAT_TOC = 0x00;
    private const byte CDROM_READ_TOC_EX_FORMAT_CDTEXT = 0x05;
    private const int TRACK_MODE_CDDA = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct CDROM_READ_TOC_EX
    {
        public byte FormatAndMsf; // bits 0..3 = Format, bit 7 = Msf (0 = LBA)
        public byte SessionTrack;
        public byte Reserved2;
        public byte Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAW_READ_INFO
    {
        public long DiskOffset;
        public uint SectorCount;
        public int TrackMode;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
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
        ref CDROM_READ_TOC_EX lpInBuffer,
        int nInBufferSize,
        [Out] byte[] lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref RAW_READ_INFO lpInBuffer,
        int nInBufferSize,
        [Out] byte[] lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    public static IReadOnlyList<string> GetCdDriveLetters()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.CDRom)
                .Select(d => d.Name.TrimEnd('\\', ':').ToUpperInvariant())
                .Where(s => s.Length == 1)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static int GetDriveIndexByLetter(string? driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter)) return -1;
        string target = driveLetter.Trim()[0].ToString().ToUpperInvariant();
        var drives = GetCdDriveLetters();
        for (int i = 0; i < drives.Count; i++)
        {
            if (drives[i].Equals(target, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    public static string? GetDriveLetterByIndex(int driveIndex)
    {
        var drives = GetCdDriveLetters();
        return driveIndex >= 0 && driveIndex < drives.Count ? drives[driveIndex] : null;
    }

    /// <summary>
    /// Reads CD-Text entries (TITLE0..TITLE99, PERFORMER0..PERFORMER99, etc.) from an Audio CD.
    /// </summary>
    public static Dictionary<string, string> ReadCdText(string driveLetter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(driveLetter)) return result;

        char letter = char.ToUpperInvariant(driveLetter.Trim()[0]);
        using var handle = OpenDevice(letter);
        if (handle.IsInvalid) return result;

        var input = new CDROM_READ_TOC_EX
        {
            FormatAndMsf = CDROM_READ_TOC_EX_FORMAT_CDTEXT,
            SessionTrack = 0
        };

        var buffer = new byte[4 + 18 * 256];
        if (!DeviceIoControl(handle, IOCTL_CDROM_READ_TOC_EX, ref input, Marshal.SizeOf<CDROM_READ_TOC_EX>(),
                buffer, buffer.Length, out uint bytesReturned, IntPtr.Zero) || bytesReturned < 22)
        {
            return result;
        }

        return ParseCdTextBuffer(buffer.AsSpan(0, (int)bytesReturned));
    }

    internal static Dictionary<string, string> ParseCdTextBuffer(ReadOnlySpan<byte> buffer)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (buffer.Length < 22) return result;

        int dataLen = ((buffer[0] << 8) | buffer[1]) + 2;
        int limit = Math.Min(buffer.Length, dataLen);

        var currentText = new Dictionary<byte, StringBuilder>();
        var currentTrack = new Dictionary<byte, int>();

        for (int offset = 4; offset + 18 <= limit; offset += 18)
        {
            byte packType = buffer[offset];
            byte trackNum = (byte)(buffer[offset + 1] & 0x7F);
            byte blockChar = buffer[offset + 3];
            int blockNumber = (blockChar >> 4) & 0x07;
            if (blockNumber != 0) continue; // Only primary language block 0

            string? keyPrefix = packType switch
            {
                0x80 => "TITLE",
                0x81 => "PERFORMER",
                0x82 => "SONGWRITER",
                0x83 => "COMPOSER",
                0x84 => "ARRANGER",
                0x85 => "MESSAGE",
                _ => null
            };
            if (keyPrefix is null) continue;

            if (!currentText.TryGetValue(packType, out var sb))
            {
                sb = new StringBuilder();
                currentText[packType] = sb;
                currentTrack[packType] = trackNum;
            }

            for (int i = 0; i < 12; i++)
            {
                byte b = buffer[offset + 4 + i];
                if (b == 0)
                {
                    int tr = currentTrack[packType];
                    string text = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(text))
                        result[$"{keyPrefix}{tr}"] = text;
                    sb.Clear();
                    currentTrack[packType] = tr + 1;
                }
                else
                {
                    sb.Append((char)b);
                }
            }
        }

        return result;
    }

    public static string? GetCdTextTrackTitle(string cdaFilePath)
    {
        try
        {
            var root = Path.GetPathRoot(cdaFilePath);
            if (string.IsNullOrWhiteSpace(root)) return null;
            var dict = ReadCdText(root);
            var nameNoExt = Path.GetFileNameWithoutExtension(cdaFilePath);
            var digits = new string(nameNoExt.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int trackNum) && dict.TryGetValue($"TITLE{trackNum}", out var title))
                return title;
        }
        catch
        {
            // Ignore CD-Text errors
        }
        return null;
    }

    /// <summary>
    /// Rips an Audio CD track directly to a standard 44.1 kHz 16-bit stereo WAV file.
    /// </summary>
    public static void RipTrackToWav(
        string driveLetter,
        int trackNumber1Based,
        string outputWavPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        int readSpeedKBps = 0)
    {
        var dir = Path.GetDirectoryName(outputWavPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var wav = new WavWriter(outputWavPath, sampleRate: 44100, channels: 2, bitsPerSample: 16);
        using var adapter = new WavWriterStream(wav);
        ReadAudioTrackPcm(driveLetter, trackNumber1Based, adapter, progress, ct, readSpeedKBps);
    }

    private sealed class WavWriterStream(WavWriter writer) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => writer.WriteBytes(buffer, offset, count);
    }

    /// <summary>
    /// Reads an Audio CD track's raw 44.1 kHz 16-bit stereo PCM data directly via IOCTL_CDROM_RAW_READ.
    /// </summary>
    public static void ReadAudioTrackPcm(
        string driveLetter,
        int trackNumber1Based,
        Stream pcmOutput,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        int readSpeedKBps = 0)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            throw new ArgumentException("Drive letter is required.", nameof(driveLetter));

        char letter = char.ToUpperInvariant(driveLetter.Trim()[0]);
        CDBurner10.Core.Native.ScsiOpticalDevice.TrySetReadSpeed(letter.ToString(), readSpeedKBps);
        using var handle = OpenDevice(letter);
        if (handle.IsInvalid)
            throw new IOException($"Could not open optical drive {letter}: for CD-DA reading.");

        if (!TryGetTrackRange(handle, trackNumber1Based, out int startLba, out int endLba))
            throw new InvalidOperationException($"Audio track {trackNumber1Based} was not found on drive {letter}:.");

        int totalSectors = Math.Max(0, endLba - startLba);
        if (totalSectors == 0) return;

        var buffer = new byte[SectorsPerRead * CdAudioSectorBytes];
        int current = startLba;
        int lastPct = -1;

        while (current < endLba)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(SectorsPerRead, endLba - current);

            var rawInfo = new RAW_READ_INFO
            {
                DiskOffset = (long)current * 2048L,
                SectorCount = (uint)count,
                TrackMode = TRACK_MODE_CDDA
            };

            int bytesExpected = count * CdAudioSectorBytes;
            if (!DeviceIoControl(handle, IOCTL_CDROM_RAW_READ, ref rawInfo, Marshal.SizeOf<RAW_READ_INFO>(),
                    buffer, bytesExpected, out uint bytesReturned, IntPtr.Zero))
            {
                throw new IOException($"Failed to read CD-DA audio sector {current} on drive {letter}: (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            pcmOutput.Write(buffer, 0, (int)bytesReturned);
            current += count;

            int pct = (int)Math.Clamp((double)(current - startLba) / totalSectors * 100.0, 0, 100);
            if (pct != lastPct)
            {
                lastPct = pct;
                progress?.Report(pct);
            }
        }
    }

    private static bool TryGetTrackRange(SafeFileHandle handle, int trackNumber1Based, out int startLba, out int endLba)
    {
        startLba = 0;
        endLba = 0;

        var input = new CDROM_READ_TOC_EX
        {
            FormatAndMsf = CDROM_READ_TOC_EX_FORMAT_TOC, // Msf=0 -> LBA
            SessionTrack = 1
        };

        var toc = new byte[4 + 100 * 8];
        if (!DeviceIoControl(handle, IOCTL_CDROM_READ_TOC_EX, ref input, Marshal.SizeOf<CDROM_READ_TOC_EX>(),
                toc, toc.Length, out uint bytesReturned, IntPtr.Zero) || bytesReturned < 12)
        {
            return false;
        }

        int dataLen = ((toc[0] << 8) | toc[1]) + 2;
        int limit = Math.Min((int)bytesReturned, dataLen);

        var tracks = new List<(int TrackNum, int StartLba)>();
        for (int offset = 4; offset + 8 <= limit; offset += 8)
        {
            int trNum = toc[offset + 2];
            int lba = (toc[offset + 4] << 24) | (toc[offset + 5] << 16) | (toc[offset + 6] << 8) | toc[offset + 7];
            tracks.Add((trNum, lba));
        }

        for (int i = 0; i < tracks.Count - 1; i++)
        {
            if (tracks[i].TrackNum == trackNumber1Based)
            {
                startLba = tracks[i].StartLba;
                endLba = tracks[i + 1].StartLba;
                return endLba > startLba;
            }
        }

        return false;
    }

    private static SafeFileHandle OpenDevice(char letter) =>
        CreateFileW(
            $@"\\.\{letter}:",
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);
}
