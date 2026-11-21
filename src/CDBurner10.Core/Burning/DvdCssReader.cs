using System.Diagnostics;
using CDBurner10.Core.Native;

namespace CDBurner10.Core.Burning;

/// <summary>
/// Reads a CSS-protected DVD through libdvdcss and writes a decrypted,
/// mountable ISO image. libdvdcss authenticates the drive and decrypts the
/// sectors, which is much faster than letting the burning SDK retry every
/// title-key switch.
/// </summary>
public static class DvdCssReader
{
    public const string DllName = LibDvdCss.DllName;
    public const int BlockSize = LibDvdCss.BlockSize;
    public const int BlocksPerRead = 64;

    public static bool IsAvailable => LibDvdCss.IsAvailable;

    public static long GetSectorCount(long byteCount) =>
        byteCount <= 0 ? 0 : (byteCount + BlockSize - 1) / BlockSize;

    public static void GrabToIso(
        DriveModel drive,
        string targetPath,
        long expectedBytes,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(drive.Letter))
            throw new InvalidOperationException("The selected drive does not report a drive letter, so libdvdcss cannot open it.");
        if (!IsAvailable)
            throw new InvalidOperationException(
                $"The bundled '{LibDvdCss.DllName}' was not found next to the application. " +
                "Install the native folder next to CDBurner10.exe or rebuild the application.");

        string directory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string devicePath = $"{drive.Letter}:";
        IntPtr dvd = LibDvdCss.dvdcss_open(devicePath);
        if (dvd == IntPtr.Zero)
            throw new InvalidOperationException($"libdvdcss could not open drive {devicePath}.");

        try
        {
            long expectedBlocks = GetSectorCount(expectedBytes);
            var buffer = new byte[BlockSize * BlocksPerRead];
            long lba = 0;
            long writtenBytes = 0;
            var stopwatch = Stopwatch.StartNew();

            using var output = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (expectedBlocks > 0 && lba >= expectedBlocks)
                    break;

                int seekResult = LibDvdCss.dvdcss_seek(dvd, (int)lba, 0);
                if (seekResult < 0)
                {
                    if (writtenBytes > 0) break;
                    throw new InvalidOperationException(
                        $"libdvdcss could not seek to sector {lba}: {LibDvdCss.GetError(dvd)}");
                }

                int blocksRequested = BlocksPerRead;
                if (expectedBlocks > 0)
                    blocksRequested = (int)Math.Min(blocksRequested, expectedBlocks - lba);

                int blocksRead = LibDvdCss.dvdcss_read(dvd, buffer, blocksRequested, LibDvdCss.ReadDecrypt);
                if (blocksRead < 0)
                    throw new InvalidOperationException(
                        $"libdvdcss failed while reading sector {lba}: {LibDvdCss.GetError(dvd)}");
                if (blocksRead == 0)
                    break;

                int bytesRead = blocksRead * BlockSize;
                output.Write(buffer, 0, bytesRead);
                lba += blocksRead;
                writtenBytes += bytesRead;

                progress?.Report(new BurnProgress(
                    BurnPhase.Grabbing,
                    ComputePercent(writtenBytes, expectedBytes),
                    TimeRemaining: EstimateRemaining(writtenBytes, expectedBytes, stopwatch.Elapsed),
                    Message: $"Decrypting CSS DVD... {ComputePercent(writtenBytes, expectedBytes)}%"));
            }

            if (writtenBytes == 0)
                throw new InvalidOperationException($"libdvdcss returned no data for drive {devicePath}.");
        }
        catch
        {
            TryDelete(targetPath);
            throw;
        }
        finally
        {
            LibDvdCss.dvdcss_close(dvd);
        }
    }

    private static int ComputePercent(long writtenBytes, long expectedBytes)
    {
        if (expectedBytes <= 0) return 0;
        return (int)Math.Clamp(writtenBytes * 100L / expectedBytes, 0, 100);
    }

    private static TimeSpan EstimateRemaining(long writtenBytes, long expectedBytes, TimeSpan elapsed)
    {
        if (expectedBytes <= writtenBytes || writtenBytes <= 0 || elapsed.TotalSeconds <= 0)
            return default;

        double bytesPerSecond = writtenBytes / elapsed.TotalSeconds;
        return bytesPerSecond <= 0
            ? default
            : TimeSpan.FromSeconds((expectedBytes - writtenBytes) / bytesPerSecond);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
