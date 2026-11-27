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

    public static BadSectorTracker GrabToIso(
        DriveModel drive,
        string targetPath,
        long expectedBytes,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default,
        int retries = 3,
        bool ignoreBadBlocks = false,
        bool disableHardwareEc = false)
    {
        if (string.IsNullOrWhiteSpace(drive.Letter))
            throw new InvalidOperationException("The selected drive does not report a drive letter, so libdvdcss cannot open it.");
        if (!IsAvailable)
            throw new InvalidOperationException(
                $"The bundled '{LibDvdCss.DllName}' was not found next to the application. " +
                "Install the native folder next to CDBurner10.exe or rebuild the application.");

        string directory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var hwEcScope = ScsiOpticalDevice.EnterHardwareErrorCorrectionScope(drive.Letter, disableHardwareEc);

        string devicePath = $"{drive.Letter}:";
        IntPtr dvd = LibDvdCss.dvdcss_open(devicePath);
        if (dvd == IntPtr.Zero)
            throw new InvalidOperationException($"libdvdcss could not open drive {devicePath}.");

        bool completed = false;
        var tracker = new BadSectorTracker();
        int maxAttempts = ScsiOpticalDevice.GetMaxReadAttempts(retries);

        try
        {
            long expectedBlocks = GetSectorCount(expectedBytes);
            var buffer = new byte[BlockSize * BlocksPerRead];
            var singleBlock = new byte[BlockSize];
            long lba = 0;
            long writtenBytes = 0;
            int lastPct = -1;
            int lastReportedErrors = 0;
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

                int blocksRequested = BlocksPerRead;
                if (expectedBlocks > 0)
                    blocksRequested = (int)Math.Min(blocksRequested, expectedBlocks - lba);

                int seekResult = LibDvdCss.dvdcss_seek(dvd, (int)lba, 0);
                if (seekResult < 0 && writtenBytes > 0 && expectedBlocks <= 0)
                    break;

                int blocksRead = seekResult >= 0
                    ? LibDvdCss.dvdcss_read(dvd, buffer, blocksRequested, LibDvdCss.ReadDecrypt)
                    : -1;

                if (blocksRead == 0)
                    break;

                if (blocksRead > 0)
                {
                    int bytesRead = blocksRead * BlockSize;
                    output.Write(buffer, 0, bytesRead);
                    lba += blocksRead;
                    writtenBytes += bytesRead;
                }
                else
                {
                    // 64-sector chunk failed — fall back to 1-sector reads with retries and optional zero-fill
                    bool reachedEndOfDisc = false;
                    long lastFallbackReportMs = 0;
                    for (int s = 0; s < blocksRequested; s++)
                    {
                        ct.ThrowIfCancellationRequested();
                        long sectorLba = lba + s;
                        if (writtenBytes > 0 && LibDvdCss.dvdcss_seek(dvd, (int)sectorLba, 0) < 0)
                        {
                            reachedEndOfDisc = true;
                            break;
                        }

                        if (TryReadSingleCssSector(dvd, (int)sectorLba, singleBlock, maxAttempts, ct))
                        {
                            output.Write(singleBlock, 0, BlockSize);
                        }
                        else
                        {
                            if (!ignoreBadBlocks)
                            {
                                throw new InvalidOperationException(
                                    $"libdvdcss failed while reading sector {sectorLba} after {maxAttempts} attempt(s): {LibDvdCss.GetError(dvd)}");
                            }

                            Array.Clear(singleBlock, 0, BlockSize);
                            output.Write(singleBlock, 0, BlockSize);
                            tracker.RecordBadSector(sectorLba);
                        }

                        writtenBytes += BlockSize;
                        lba = sectorLba + 1;

                        long nowMs = stopwatch.ElapsedMilliseconds;
                        if (nowMs - lastFallbackReportMs >= 500 || tracker.BadSectorCount != lastReportedErrors)
                        {
                            lastFallbackReportMs = nowMs;
                            lastReportedErrors = tracker.BadSectorCount;
                            int curPct = ComputePercent(writtenBytes, expectedBytes);
                            lastPct = curPct;
                            string errSuffix = tracker.BadSectorCount > 0
                                ? $" (Read errors: {tracker.BadSectorCount})"
                                : string.Empty;
                            progress?.Report(new BurnProgress(
                                BurnPhase.Grabbing,
                                curPct,
                                TimeRemaining: EstimateRemaining(writtenBytes, expectedBytes, stopwatch.Elapsed),
                                Message: $"Decrypting CSS DVD... {curPct}% (sector {sectorLba:N0}){errSuffix}",
                                ReadErrors: tracker.BadSectorCount));
                        }
                    }

                    if (reachedEndOfDisc)
                        break;
                }

                int pct = ComputePercent(writtenBytes, expectedBytes);
                if (pct != lastPct || tracker.BadSectorCount != lastReportedErrors)
                {
                    lastPct = pct;
                    lastReportedErrors = tracker.BadSectorCount;
                    string errSuffix = tracker.BadSectorCount > 0
                        ? $" (Read errors: {tracker.BadSectorCount})"
                        : string.Empty;
                    progress?.Report(new BurnProgress(
                        BurnPhase.Grabbing,
                        pct,
                        TimeRemaining: EstimateRemaining(writtenBytes, expectedBytes, stopwatch.Elapsed),
                        Message: $"Decrypting CSS DVD... {pct}%{errSuffix}",
                        ReadErrors: tracker.BadSectorCount));
                }
            }

            if (writtenBytes == 0)
                throw new InvalidOperationException($"libdvdcss returned no data for drive {devicePath}.");

            completed = true;
            return tracker;
        }
        finally
        {
            LibDvdCss.dvdcss_close(dvd);
            if (!completed)
                TryDelete(targetPath);
        }
    }

    private static bool TryReadSingleCssSector(
        IntPtr dvd,
        int sectorLba,
        byte[] singleBlock,
        int maxAttempts,
        CancellationToken ct)
    {
        int attempts = Math.Max(1, maxAttempts);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (LibDvdCss.dvdcss_seek(dvd, sectorLba, 0) >= 0 &&
                LibDvdCss.dvdcss_read(dvd, singleBlock, 1, LibDvdCss.ReadDecrypt) == 1)
            {
                return true;
            }

            if (attempt + 1 < attempts)
                Thread.Sleep(25);
        }

        return false;
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
