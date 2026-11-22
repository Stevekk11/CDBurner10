using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CDBurner10.Core.Burning;

public sealed record UnfinalizedDvdScanResult(
    int FirstMpegSector,
    long EstimatedTotalBytes,
    long EstimatedTotalSectors);

public sealed record UnfinalizedDvdReadResult(
    int StartSector,
    int EndSector,
    long SectorsWritten,
    long BytesWritten,
    long SkippedUnreadableSectors,
    TimeSpan Elapsed);

/// <summary>
/// Recovers raw MPEG-2 Program Stream (00 00 01 BA) sectors directly from
/// unfinalized DVD-R / DVD+R / DVD-RW discs (such as camcorder or standalone
/// DVD recorder discs where sectors 0..1535 were left unwritten awaiting
/// finalization and no UDF/VIDEO_TS.IFO filesystem exists).
/// </summary>
public static class UnfinalizedDvdReader
{
    public const string DllName = "libdvdcss-2.dll";
    public const int BlockSize = 2048;
    public const int EccBlockSectors = 16;
    public const int ChunkSectors = 64;
    private const int MaxInitialScanSectors = 65536; // First 128 MB
    private const int MaxTrailingUnreadableSectors = 2048; // 4 MB of blank media -> end of recording

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr dvdcss_open([MarshalAs(UnmanagedType.LPUTF8Str)] string target);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int dvdcss_seek(IntPtr dvdcss, int blocks, int flags);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int dvdcss_read(IntPtr dvdcss, [Out] byte[] buffer, int blocks, int flags);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int dvdcss_close(IntPtr dvdcss);

    public static bool IsAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, DllName));

    /// <summary>
    /// Checks whether a 2048-byte sector begins with an MPEG-2 Program Stream
    /// Pack Start Code (00 00 01 BA).
    /// </summary>
    public static bool IsMpeg2PackSector(ReadOnlySpan<byte> sector) =>
        sector.Length >= 4 &&
        sector[0] == 0x00 &&
        sector[1] == 0x00 &&
        sector[2] == 0x01 &&
        sector[3] == 0xBA;

    /// <summary>
    /// Scans an optical drive for raw MPEG-2 Program Stream sectors on an unfinalized DVD.
    /// </summary>
    public static bool TryScanDrive(string driveLetter, out UnfinalizedDvdScanResult? result)
    {
        result = null;
        if (!IsAvailable || string.IsNullOrWhiteSpace(driveLetter))
            return false;

        char letter = char.ToUpperInvariant(driveLetter.Trim()[0]);
        IntPtr dvd = dvdcss_open($"{letter}:");
        if (dvd == IntPtr.Zero)
            return false;

        try
        {
            var buf = new byte[BlockSize * EccBlockSectors];
            int firstReadableBlock = -1;

            // Fast probe common DVD recorder / camcorder data-start LBAs first so we avoid
            // dozens of slow SCSI read-error retries on unwritten sectors 0..1535.
            int[] quickProbes = [1536, 2048, 2560, 3072, 4096, 8192, 16384, 32768, 512, 1024, 0];
            foreach (int probe in quickProbes)
            {
                if (dvdcss_seek(dvd, probe, 0) >= 0 && dvdcss_read(dvd, buf, EccBlockSectors, 0) > 0)
                {
                    firstReadableBlock = probe;
                    break;
                }
            }

            if (firstReadableBlock > 0)
            {
                // Check the immediately preceding ECC block first; if it is unreadable,
                // firstReadableBlock is already the exact start (avoids ~6 slow SCSI error retries).
                int prevLba = firstReadableBlock - EccBlockSectors;
                if (dvdcss_seek(dvd, prevLba, 0) >= 0 && dvdcss_read(dvd, buf, EccBlockSectors, 0) > 0)
                {
                    int lo = 0;
                    int hi = prevLba / EccBlockSectors;
                    while (lo < hi)
                    {
                        int mid = (lo + hi) / 2;
                        int lba = mid * EccBlockSectors;
                        if (dvdcss_seek(dvd, lba, 0) >= 0 && dvdcss_read(dvd, buf, EccBlockSectors, 0) > 0)
                            hi = mid;
                        else
                            lo = mid + 1;
                    }
                    firstReadableBlock = lo * EccBlockSectors;
                }
            }
            else if (firstReadableBlock < 0)
            {
                // Coarse scan across first 128 MB in 256-sector (512 KB) steps.
                for (int lba = 0; lba < MaxInitialScanSectors; lba += 256)
                {
                    if (dvdcss_seek(dvd, lba, 0) >= 0 && dvdcss_read(dvd, buf, EccBlockSectors, 0) > 0)
                    {
                        firstReadableBlock = lba;
                        break;
                    }
                }
            }

            if (firstReadableBlock < 0)
                return false;

            int firstMpeg = -1;
            int scanLimit = Math.Min(MaxInitialScanSectors, firstReadableBlock + 4096);
            for (int lba = firstReadableBlock; lba < scanLimit; lba += EccBlockSectors)
            {
                if (dvdcss_seek(dvd, lba, 0) < 0) continue;
                int read = dvdcss_read(dvd, buf, EccBlockSectors, 0);
                if (read <= 0) continue;

                for (int i = 0; i < read; i++)
                {
                    if (IsMpeg2PackSector(buf.AsSpan(i * BlockSize, BlockSize)))
                    {
                        firstMpeg = lba + i;
                        break;
                    }
                }
                if (firstMpeg >= 0) break;
            }

            if (firstMpeg < 0)
                return false;

            long estimatedBytes = GetEstimatedDiscUsedBytes(letter.ToString());
            long estimatedSectors = estimatedBytes > 0 ? estimatedBytes / BlockSize : 0;
            result = new UnfinalizedDvdScanResult(firstMpeg, estimatedBytes, estimatedSectors);
            return true;
        }
        finally
        {
            dvdcss_close(dvd);
        }
    }

    /// <summary>
    /// Reads a small MPEG-2 sample (default 4 MB) from the first readable MPEG-2 sector
    /// into <paramref name="outputPath"/> so ffprobe can inspect video/audio codecs and bitrate.
    /// </summary>
    public static long ReadSampleToFile(
        string driveLetter,
        string outputPath,
        int startSector,
        long maxBytes = 4 * 1024 * 1024,
        CancellationToken ct = default)
    {
        char letter = char.ToUpperInvariant(driveLetter.Trim()[0]);
        IntPtr dvd = dvdcss_open($"{letter}:");
        if (dvd == IntPtr.Zero)
            throw new InvalidOperationException($"Could not open drive {letter}: for raw DVD reading.");

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024);
            var buf = new byte[BlockSize * EccBlockSectors];
            long written = 0;
            int lba = Math.Max(0, startSector);
            int maxScan = lba + (int)(maxBytes / BlockSize) * 4;

            while (written < maxBytes && lba < maxScan)
            {
                ct.ThrowIfCancellationRequested();
                if (dvdcss_seek(dvd, lba, 0) >= 0)
                {
                    int r = dvdcss_read(dvd, buf, EccBlockSectors, 0);
                    if (r > 0)
                    {
                        for (int i = 0; i < r && written < maxBytes; i++)
                        {
                            var slice = buf.AsSpan(i * BlockSize, BlockSize);
                            if (IsMpeg2PackSector(slice))
                            {
                                fs.Write(slice);
                                written += BlockSize;
                            }
                        }
                        lba += r;
                        continue;
                    }
                }
                lba += EccBlockSectors;
            }

            return written;
        }
        finally
        {
            dvdcss_close(dvd);
        }
    }

    /// <summary>
    /// Streams all readable MPEG-2 Program Stream sectors from an unfinalized DVD into <paramref name="output"/>,
    /// skipping unwritten gaps cleanly.
    /// </summary>
    public static UnfinalizedDvdReadResult ReadToStream(
        string driveLetter,
        Stream output,
        UnfinalizedDvdScanResult? scan = null,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        char letter = char.ToUpperInvariant(driveLetter.Trim()[0]);
        if (scan is null)
        {
            if (!TryScanDrive(letter.ToString(), out scan) || scan is null)
                throw new InvalidOperationException($"No readable MPEG-2 video sectors were found on drive {letter}:\\.");
        }

        IntPtr dvd = dvdcss_open($"{letter}:");
        if (dvd == IntPtr.Zero)
            throw new InvalidOperationException($"Could not open drive {letter}: for raw DVD reading.");

        try
        {
            long maxSector = scan.EstimatedTotalSectors > scan.FirstMpegSector
                ? scan.EstimatedTotalSectors + 2048
                : 2_300_000; // ~4.7 GB upper bound if drive did not report UsedSize

            var chunkBuf = new byte[BlockSize * ChunkSectors];
            var eccBuf = new byte[BlockSize * EccBlockSectors];
            int lba = scan.FirstMpegSector;
            long sectorsWritten = 0;
            long bytesWritten = 0;
            long skippedSectors = 0;
            int consecutiveUnreadable = 0;
            int lastPercent = -1;
            var sw = Stopwatch.StartNew();

            progress?.Report(new BurnProgress(
                BurnPhase.Grabbing,
                0,
                Message: $"Reading unfinalized DVD-R from sector {lba:N0}..."));

            while (lba < maxSector)
            {
                ct.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(ChunkSectors, maxSector - lba);
                int read = dvdcss_seek(dvd, lba, 0) >= 0
                    ? dvdcss_read(dvd, chunkBuf, toRead, 0)
                    : -1;

                if (read > 0)
                {
                    consecutiveUnreadable = 0;
                    WriteMpegSectors(chunkBuf, read, output, ref sectorsWritten, ref bytesWritten);
                    lba += read;
                }
                else
                {
                    // Fall back to 16-sector ECC blocks across gaps or end-of-recording boundary.
                    int eccRead = dvdcss_seek(dvd, lba, 0) >= 0
                        ? dvdcss_read(dvd, eccBuf, EccBlockSectors, 0)
                        : -1;

                    if (eccRead > 0)
                    {
                        consecutiveUnreadable = 0;
                        WriteMpegSectors(eccBuf, eccRead, output, ref sectorsWritten, ref bytesWritten);
                        lba += eccRead;
                    }
                    else
                    {
                        lba += EccBlockSectors;
                        skippedSectors += EccBlockSectors;
                        consecutiveUnreadable += EccBlockSectors;

                        if (sectorsWritten > 0 && consecutiveUnreadable >= MaxTrailingUnreadableSectors)
                            break;
                    }
                }

                if (progress is not null && scan.EstimatedTotalSectors > scan.FirstMpegSector)
                {
                    long totalRange = scan.EstimatedTotalSectors - scan.FirstMpegSector;
                    long done = Math.Max(0, lba - scan.FirstMpegSector);
                    int pct = (int)Math.Clamp((double)done / totalRange * 100.0, 0, 99);
                    if (pct != lastPercent)
                    {
                        lastPercent = pct;
                        TimeSpan rem = TimeSpan.Zero;
                        if (done > 0 && done < totalRange && sw.Elapsed.TotalSeconds > 0.5)
                        {
                            double rate = done / sw.Elapsed.TotalSeconds;
                            if (rate > 0) rem = TimeSpan.FromSeconds((totalRange - done) / rate);
                        }
                        progress.Report(new BurnProgress(
                            BurnPhase.Grabbing,
                            pct,
                            TimeRemaining: rem,
                            Message: $"Reading unfinalized DVD-R... {pct}%"));
                    }
                }
            }

            output.Flush();
            progress?.Report(new BurnProgress(
                BurnPhase.Completed,
                100,
                Message: $"Recovered {sectorsWritten:N0} MPEG-2 sectors ({bytesWritten / (1024.0 * 1024.0):0.1} MB) from unfinalized DVD."));

            return new UnfinalizedDvdReadResult(
                scan.FirstMpegSector,
                lba,
                sectorsWritten,
                bytesWritten,
                skippedSectors,
                sw.Elapsed);
        }
        finally
        {
            dvdcss_close(dvd);
        }
    }

    public static UnfinalizedDvdReadResult ReadToFile(
        string driveLetter,
        string outputPath,
        UnfinalizedDvdScanResult? scan = null,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);

        return ReadToStream(driveLetter, fs, scan, progress, ct);
    }

    private static void WriteMpegSectors(
        byte[] buffer,
        int sectorCount,
        Stream output,
        ref long sectorsWritten,
        ref long bytesWritten)
    {
        // Fast path: if all sectors in this chunk are MPEG-2 pack sectors, write in one call.
        bool allMpeg = true;
        for (int i = 0; i < sectorCount; i++)
        {
            if (!IsMpeg2PackSector(buffer.AsSpan(i * BlockSize, BlockSize)))
            {
                allMpeg = false;
                break;
            }
        }

        if (allMpeg)
        {
            int totalBytes = sectorCount * BlockSize;
            output.Write(buffer, 0, totalBytes);
            sectorsWritten += sectorCount;
            bytesWritten += totalBytes;
            return;
        }

        for (int i = 0; i < sectorCount; i++)
        {
            var slice = buffer.AsSpan(i * BlockSize, BlockSize);
            if (IsMpeg2PackSector(slice))
            {
                output.Write(slice);
                sectorsWritten++;
                bytesWritten += BlockSize;
            }
        }
    }

    private static long GetEstimatedDiscUsedBytes(string driveLetter)
    {
        try
        {
            if (!BurningEngine.IsInitialized) return 0;
            var drive = BurningEngine.GetDriveByLetter(driveLetter);
            if (drive is null) return 0;
            var info = drive.GetDiscInfo(refresh: false);
            return info.UsedSizeBytes > 0 ? info.UsedSizeBytes : info.TotalSizeBytes;
        }
        catch
        {
            return 0;
        }
    }
}
