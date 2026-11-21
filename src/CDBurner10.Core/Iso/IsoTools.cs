using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace CDBurner10.Core.Iso;

public static class IsoTools
{
    private const uint DRIVE_REMOVABLE = 2;
    private const uint DRIVE_CDROM = 5;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string lpRootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out long lpFreeBytesAvailable,
        out long lpTotalNumberOfBytes,
        out long lpTotalNumberOfFreeBytes);

    public static bool IsFloppyDrive(string driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter)) return false;
        string root = $"{char.ToUpperInvariant(driveLetter.Trim()[0])}:\\";
        return GetDriveType(root) == DRIVE_REMOVABLE;
    }

    public static bool IsNrgAudioImage(string nrgPath)
    {
        if (!File.Exists(nrgPath)) return false;
        using var fs = File.OpenRead(nrgPath);
        if (fs.Length < 16) return false;

        byte[] footer = new byte[12];
        fs.Seek(-12, SeekOrigin.End);
        if (fs.Read(footer, 0, 12) < 12) return false;

        string sig = Encoding.ASCII.GetString(footer, 0, 4);
        long offset = sig == "NER5"
            ? BinaryPrimitives.ReadInt64BigEndian(footer.AsSpan(4, 8))
            : BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(8, 4));

        if (offset < 0 || offset + 4 > fs.Length) return false;

        fs.Seek(offset, SeekOrigin.Begin);
        byte[] tag = new byte[4];
        if (fs.Read(tag, 0, 4) < 4) return false;
        string tagStr = Encoding.ASCII.GetString(tag);
        return tagStr is "CUES" or "CUEX";
    }

    public static Task ConvertToIsoAsync(
        string inputPath,
        string outputIsoPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        return ext switch
        {
            ".bin" => ConvertBinToIsoAsync(inputPath, outputIsoPath, progress, ct),
            ".nrg" => ConvertNrgToIsoAsync(inputPath, outputIsoPath, progress, ct),
            ".b5i" => ConvertB5iToIsoAsync(inputPath, outputIsoPath, progress, ct),
            _ => throw new NotSupportedException($"Unsupported image format '{ext}'. Supported formats: .bin, .nrg, .b5i.")
        };
    }

    public static async Task ConvertBinToIsoAsync(
        string inputPath,
        string outputIsoPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        byte[] header = new byte[16];
        await using (var probe = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await probe.ReadExactlyAsync(header.AsMemory(0, Math.Min(16, (int)Math.Min(16, probe.Length))), ct);
        }

        bool isDvdBin = header.AsSpan(0, 12).SequenceEqual(new byte[12]);
        if (isDvdBin)
        {
            await ConvertBinToDvdIsoAsync(inputPath, outputIsoPath, progress, ct);
        }
        else
        {
            await ConvertBinToCdIsoAsync(inputPath, outputIsoPath, progress, ct);
        }
    }

    private static async Task ConvertBinToDvdIsoAsync(
        string inputPath,
        string outputIsoPath,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        const int blockSize = 37856;
        byte[] buf = new byte[182];

        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write, FileShare.None);

        long totalBlocks = input.Length / blockSize;
        if (totalBlocks <= 0)
        {
            progress?.Report(100);
            return;
        }

        for (long block = 0; block < totalBlocks; block++)
        {
            ct.ThrowIfCancellationRequested();

            for (int sector = 0; sector < 16; sector++)
            {
                for (int row = 0; row < 12; row++)
                {
                    await input.ReadExactlyAsync(buf.AsMemory(0, 182), ct);
                    await output.WriteAsync(buf.AsMemory(12, 160), ct);

                    for (int mid = 0; mid < 10; mid++)
                    {
                        await input.ReadExactlyAsync(buf.AsMemory(0, 182), ct);
                        await output.WriteAsync(buf.AsMemory(0, 172), ct);
                    }

                    await input.ReadExactlyAsync(buf.AsMemory(0, 182), ct);
                    await output.WriteAsync(buf.AsMemory(0, 168), ct);
                }
            }

            input.Seek(2912, SeekOrigin.Current);
            progress?.Report((block + 1) * 100.0 / totalBlocks);
        }

        await output.FlushAsync(ct);
    }

    private static async Task ConvertBinToCdIsoAsync(
        string inputPath,
        string outputIsoPath,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        byte[] cdSync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        byte[] sectorBuf = new byte[2352];

        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write, FileShare.None);

        int read = await input.ReadAsync(sectorBuf.AsMemory(0, 16), ct);
        if (read < 16) throw new InvalidDataException("BIN file is too small.");

        int headerSkip;
        int trailerSkip;
        int sectorSize;

        if (!sectorBuf.AsSpan(0, 12).SequenceEqual(cdSync))
        {
            // Mode 2 / 2336-byte sector
            headerSkip = 8;
            trailerSkip = 280;
            sectorSize = 2336;
        }
        else if (sectorBuf[15] == 2)
        {
            // Mode 2 / 2352-byte sector
            headerSkip = 24;
            trailerSkip = 280;
            sectorSize = 2352;
        }
        else if (sectorBuf[15] == 1)
        {
            // Mode 1 / 2352-byte sector
            headerSkip = 16;
            trailerSkip = 288;
            sectorSize = 2352;
        }
        else
        {
            throw new NotSupportedException($"Unsupported BIN track mode: {sectorBuf[15]}.");
        }

        long totalSectors = input.Length / sectorSize;
        input.Seek(0, SeekOrigin.Begin);
        int lastPct = -1;

        for (long i = 0; i < totalSectors; i++)
        {
            ct.ThrowIfCancellationRequested();
            input.Seek(headerSkip, SeekOrigin.Current);
            int bytesRead = await input.ReadAsync(sectorBuf.AsMemory(0, 2048), ct);
            if (bytesRead < 2048) break;
            await output.WriteAsync(sectorBuf.AsMemory(0, 2048), ct);
            input.Seek(trailerSkip, SeekOrigin.Current);

            int pct = (int)((i + 1) * 100 / totalSectors);
            if (pct != lastPct)
            {
                lastPct = pct;
                progress?.Report(pct);
            }
        }

        await output.FlushAsync(ct);
        progress?.Report(100);
    }

    public static async Task ConvertNrgToIsoAsync(
        string inputPath,
        string outputIsoPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        const int nrgHeaderSize = 307200;
        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write, FileShare.None);

        if (input.Length <= nrgHeaderSize)
            throw new InvalidDataException("NRG file is smaller than the expected Nero header size.");

        long dataEnd = input.Length;
        if (input.Length >= 20)
        {
            byte[] footer = new byte[20];
            input.Seek(-20, SeekOrigin.End);
            await input.ReadExactlyAsync(footer, ct);

            // Check NER5 (offset 8) or NERO (offset 12)
            if ((BitConverter.ToInt32(footer, 0) == 558124613 && BitConverter.ToInt32(footer, 8) == 894584142) ||
                (BitConverter.ToInt32(footer, 4) == 558124613 && BitConverter.ToInt32(footer, 12) == 1330791758))
            {
                long footerOffset = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(footer, 16));
                if (footerOffset > nrgHeaderSize && footerOffset <= input.Length)
                {
                    dataEnd = footerOffset;
                }
            }
        }

        long remaining = dataEnd - nrgHeaderSize;
        long total = remaining;
        input.Seek(nrgHeaderSize, SeekOrigin.Begin);

        byte[] buffer = new byte[1024 * 1024];
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(remaining, buffer.Length);
            int bytesRead = await input.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (bytesRead == 0) break;

            await output.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            remaining -= bytesRead;
            progress?.Report((total - remaining) * 100.0 / total);
        }

        await output.FlushAsync(ct);
        progress?.Report(100);
    }

    public static async Task ConvertB5iToIsoAsync(
        string inputPath,
        string outputIsoPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        byte[] allFf = Enumerable.Repeat((byte)0xFF, 12).ToArray();
        byte[] buffer = new byte[2448];

        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write, FileShare.None);

        if (input.Length < 2368)
            throw new InvalidDataException("B5I file is too small.");

        input.Seek(2352, SeekOrigin.Begin);
        await input.ReadExactlyAsync(buffer.AsMemory(0, 16), ct);

        int trailerSkip;
        int sectorSize;
        if (!buffer.AsSpan(0, 12).SequenceEqual(allFf))
        {
            trailerSkip = 384;
            sectorSize = 2448;
        }
        else
        {
            trailerSkip = 288;
            sectorSize = 2352;
        }

        long totalSectors = input.Length / sectorSize;
        input.Seek(0, SeekOrigin.Begin);
        int lastPct = -1;

        for (long i = 0; i < totalSectors; i++)
        {
            ct.ThrowIfCancellationRequested();
            input.Seek(16, SeekOrigin.Current);
            int bytesRead = await input.ReadAsync(buffer.AsMemory(0, 2048), ct);
            if (bytesRead < 2048) break;

            // Skip 150-sector pregap
            if (i >= 150)
            {
                await output.WriteAsync(buffer.AsMemory(0, 2048), ct);
            }

            input.Seek(trailerSkip, SeekOrigin.Current);
            int pct = (int)((i + 1) * 100 / totalSectors);
            if (pct != lastPct)
            {
                lastPct = pct;
                progress?.Report(pct);
            }
        }

        await output.FlushAsync(ct);
        progress?.Report(100);
    }

    public static async Task ReadImageFromFloppyDeviceAsync(
        string driveLetter,
        string outputImagePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            throw new ArgumentException("Drive letter is required.", nameof(driveLetter));

        char letter = char.ToUpperInvariant(driveLetter.Trim()[0]);
        string root = $"{letter}:\\";
        uint driveType = GetDriveType(root);
        if (driveType != DRIVE_REMOVABLE && driveType != DRIVE_CDROM)
            throw new InvalidOperationException($"Drive {letter}: is not a removable/floppy or optical drive.");

        GetDiskFreeSpaceEx(root, out _, out long totalBytes, out _);
        if (totalBytes <= 0) totalBytes = 1474560; // Standard 1.44 MB floppy fallback

        string devicePath = $@"\\.\{letter}:";
        using var handle = CreateFile(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException($"Could not open raw device '{devicePath}' (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        await using var rawStream = new FileStream(handle, FileAccess.Read);
        await using var output = new FileStream(outputImagePath, FileMode.Create, FileAccess.Write, FileShare.None);

        byte[] buffer = new byte[4096];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await rawStream.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;
            progress?.Report(Math.Min(100.0, totalRead * 100.0 / totalBytes));
        }

        await output.FlushAsync(ct);
        progress?.Report(100);
    }
}
