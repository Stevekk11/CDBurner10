using System.Buffers.Binary;
using System.Text;

namespace CDBurner10.Core.Dvd;

/// <summary>
/// Input description for a single MPEG-2 DVD-compliant title stream to be authored into a VIDEO_TS structure.
/// </summary>
public sealed record AuthoredTitleInput(
    string MpgPath,
    string TitleName,
    TimeSpan Duration,
    DvdVideoStandard Standard,
    DvdAspectRatio AspectRatio,
    int AudioStreamCount);

/// <summary>
/// Pure-C# DVD-Video <c>VIDEO_TS</c> authorer.
/// Reads FFmpeg <c>-f dvd</c> MPEG-2 Program Streams (2048-byte packs with empty NAV packs),
/// patches PCI/DSI navigation packets in place, splits VOB files at &lt;= 1 GiB sector boundaries,
/// and generates spec-compliant <c>VTS_01_0.IFO/BUP</c> and <c>VIDEO_TS.IFO/BUP</c>.
/// </summary>
public static class VideoTsAuthorer
{
    private const int SectorSize = 2048;
    private const int MaxVobSectorsPerFile = 524_287; // 1,073,739,776 bytes (< 1 GiB, aligned to 2048)
    private const int IoBufferSectors = 256;          // 512 KiB streaming buffer
    private const int IoBufferBytes = IoBufferSectors * SectorSize;

    // Split chapters every 5 minutes (90 kHz clock ticks).
    private const ulong ChapterIntervalTicks = 5UL * 60UL * 90_000UL;
    private const int MaxCellsPerTitle = 99;

    // NAV pack layout constants (standard DVD-Video 2048-byte NAV pack).
    private const int PciPacketOffset = 38;   // 00 00 01 BF 03 D4 00
    private const int PciDataOffset = 45;     // pci_gi starts at byte 45
    private const int DsiPacketOffset = 1024; // 00 00 01 BF 03 FA 01
    private const int DsiDataOffset = 1031;   // dsi_gi starts at byte 1031
    private const int DsiVobuSriOffset = DsiDataOffset + 0xEA; // 1265
    private const int DsiSynciOffset = DsiDataOffset + 0x192;  // 1433

    private const uint SriEndOfCell = 0x3FFF_FFFFu;
    private const uint SriNoVideo = 0xBFFF_FFFFu;
    private const uint SriValidFlag = 0x8000_0000u;

    // VOBU_SRI time offsets in 90 kHz ticks:
    // fwda[0..18]: +120s, +60s, +30s, +10s, +7.5s, +7.0s, ..., +1.0s, +0.5s
    private static readonly double[] FwdaSeconds =
    [
        120.0, 60.0, 30.0, 10.0,
        7.5, 7.0, 6.5, 6.0, 5.5, 5.0, 4.5, 4.0, 3.5, 3.0, 2.5, 2.0, 1.5, 1.0, 0.5
    ];

    // bwda[0..18]: -0.5s, -1.0s, ..., -7.5s, -10s, -30s, -60s, -120s
    private static readonly double[] BwdaSeconds =
    [
        0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5, 5.0, 5.5, 6.0, 6.5, 7.0, 7.5,
        10.0, 30.0, 60.0, 120.0
    ];

    // Standard 16-color YUV subpicture palette for PGC headers.
    private static readonly uint[] DefaultPgcPalette =
    [
        0x00108080, 0x00EB8080, 0x0029F06E, 0x00515AF0,
        0x00902235, 0x00D21092, 0x006ADDCA, 0x00AA10A6,
        0x00808080, 0x00C08080, 0x00408080, 0x00E08080,
        0x00608080, 0x00A08080, 0x00208080, 0x00F08080
    ];

    private sealed class VobuRecord
    {
        public uint Lba;
        public uint SectorCount;
        public ulong ScrBase;
        public ulong? MinVideoPts;
        public ulong? MaxVideoPts;
        public ulong StartPtm;
        public ulong EndPtm;
        public ulong CellElapsedTicks;
        public uint FirstRefEndOffset;
        public ushort VobId;
        public byte CellId;
        public readonly ushort[] AudioFirstOffset = [0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF];
    }

    private sealed class CellRecord
    {
        public ushort VobId;
        public byte CellId;
        public int FirstVobuIndex;
        public int LastVobuIndex;
        public uint FirstLba;
        public uint LastVobuLba;
        public uint LastSector;
        public ulong DurationTicks;
    }

    private sealed class TitlePlan
    {
        public required AuthoredTitleInput Input { get; init; }
        public ushort VobId { get; init; }
        public long SourcePackOffset { get; set; }
        public long SourcePackCount { get; set; }
        public List<VobuRecord> Vobus { get; } = [];
        public List<CellRecord> Cells { get; } = [];
        public ulong TotalDurationTicks { get; set; }
    }

    /// <summary>
    /// Authors a DVD-Video folder hierarchy (<c>VIDEO_TS</c> and <c>AUDIO_TS</c>) under <paramref name="outputRootDir"/>.
    /// Returns the path to the created <c>VIDEO_TS</c> directory.
    /// </summary>
    public static Task<string> AuthorAsync(
        IReadOnlyList<AuthoredTitleInput> titles,
        string outputRootDir,
        string discTitle,
        bool createMenu,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (titles is null || titles.Count == 0)
            throw new ArgumentException("At least one title is required to author a DVD-Video disc.", nameof(titles));
        if (titles.Count > 99)
            throw new ArgumentException("DVD-Video supports at most 99 titles per titleset.", nameof(titles));
        if (string.IsNullOrWhiteSpace(outputRootDir))
            throw new ArgumentException("Output root directory is required.", nameof(outputRootDir));

        return Task.Run(() => AuthorCore(titles, outputRootDir, discTitle, createMenu, progress, ct), ct);
    }

    private static string AuthorCore(
        IReadOnlyList<AuthoredTitleInput> titles,
        string outputRootDir,
        string discTitle,
        bool createMenu,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // Note: createMenu is accepted for API compatibility; VMG First-Play PGC jumps directly to Title 1
        // when no menu VOB is present, ensuring universal playback across all DVD players and libdvdnav.
        _ = createMenu;

        string videoTsDir = Path.Combine(outputRootDir, "VIDEO_TS");
        string audioTsDir = Path.Combine(outputRootDir, "AUDIO_TS");
        Directory.CreateDirectory(videoTsDir);
        Directory.CreateDirectory(audioTsDir);

        long totalInputBytes = 0;
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title.MpgPath) || !File.Exists(title.MpgPath))
                throw new FileNotFoundException($"Encoded MPEG-2 file not found: '{title.MpgPath}'.", title.MpgPath);
            totalInputBytes += new FileInfo(title.MpgPath).Length;
        }
        if (totalInputBytes < SectorSize)
            throw new InvalidOperationException("Input MPEG-2 streams are empty.");

        var plans = new List<TitlePlan>(titles.Count);
        uint currentLba = 0;
        long scannedBytes = 0;

        progress?.Report(0.0);

        // Pass 1: Scan all MPEG-2 streams to locate every VOBU, extract PTS/SCR/audio/reference offsets, and plan chapters.
        for (int t = 0; t < titles.Count; t++)
        {
            ct.ThrowIfCancellationRequested();
            var input = titles[t];
            var plan = new TitlePlan
            {
                Input = input,
                VobId = (ushort)(t + 1)
            };

            currentLba = ScanTitleMpg(plan, currentLba, scannedBytes, totalInputBytes, progress, ct);
            scannedBytes += new FileInfo(input.MpgPath).Length;

            if (plan.Vobus.Count == 0)
                throw new InvalidOperationException(
                    $"No DVD NAV packs were found in '{Path.GetFileName(input.MpgPath)}'. Ensure the stream was muxed with FFmpeg '-f dvd'.");

            FinalizeTitleTimingAndChapters(plan);
            plans.Add(plan);
        }

        uint totalVobSectors = currentLba;

        // Pass 2: Stream MPEG-2 packs again, patching every NAV pack's PCI + DSI in place, and split across VTS_01_1..n.VOB.
        WritePatchedVobFiles(plans, videoTsDir, totalInputBytes, progress, ct);

        progress?.Report(0.95);

        // Build and write VTS_01_0.IFO and identical VTS_01_0.BUP.
        DvdVideoStandard discStandard = titles[0].Standard;
        DvdAspectRatio discAspect = titles[0].AspectRatio == DvdAspectRatio.Auto
            ? DvdAspectRatio.Widescreen16x9
            : titles[0].AspectRatio;
        int discAudioStreams = Math.Clamp(titles.Max(t => Math.Max(1, t.AudioStreamCount)), 1, 8);

        byte[] vtsIfo = BuildVtsIfo(plans, totalVobSectors, discStandard, discAspect, discAudioStreams, out byte[] vtsMatSector);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.IFO"), vtsIfo);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.BUP"), vtsIfo);

        // Build and write VIDEO_TS.IFO and identical VIDEO_TS.BUP.
        byte[] vmgIfo = BuildVmgIfo(plans, discTitle, vtsMatSector);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.IFO"), vmgIfo);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.BUP"), vmgIfo);

        progress?.Report(1.0);
        return videoTsDir;
    }

    private static uint ScanTitleMpg(
        TitlePlan plan,
        uint startLba,
        long bytesBeforeTitle,
        long totalInputBytes,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        byte[] buffer = new byte[IoBufferBytes];
        uint lba = startLba;
        VobuRecord? currentVobu = null;
        int pictureCountInVobu = 0;
        bool firstNavSeen = false;
        long packIndexInFile = 0;
        long bytesReadInTitle = 0;

        using var fs = new FileStream(plan.Input.MpgPath, FileMode.Open, FileAccess.Read, FileShare.Read, IoBufferBytes, FileOptions.SequentialScan);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = ReadExactSectors(fs, buffer);
            if (read == 0)
                break;

            int sectorCount = read / SectorSize;
            for (int s = 0; s < sectorCount; s++)
            {
                ReadOnlySpan<byte> pack = buffer.AsSpan(s * SectorSize, SectorSize);
                if (IsNavPack(pack))
                {
                    if (!firstNavSeen)
                    {
                        firstNavSeen = true;
                        plan.SourcePackOffset = packIndexInFile;
                    }

                    currentVobu = new VobuRecord
                    {
                        Lba = lba,
                        SectorCount = 1,
                        ScrBase = ReadScrBase(pack),
                        FirstRefEndOffset = 0,
                        VobId = plan.VobId,
                        CellId = 1
                    };
                    plan.Vobus.Add(currentVobu);
                    pictureCountInVobu = 0;
                    lba++;
                    plan.SourcePackCount++;
                }
                else if (currentVobu is not null)
                {
                    uint relOffset = currentVobu.SectorCount;
                    currentVobu.SectorCount++;
                    lba++;
                    plan.SourcePackCount++;

                    InspectNonNavPack(pack, currentVobu, relOffset, ref pictureCountInVobu);
                }

                packIndexInFile++;
            }

            bytesReadInTitle += read;
            if (progress is not null && totalInputBytes > 0)
            {
                double scanFraction = Math.Clamp((bytesBeforeTitle + bytesReadInTitle) / (double)totalInputBytes, 0.0, 1.0);
                progress.Report(scanFraction * 0.45);
            }
        }

        // Default FirstRefEndOffset for any VOBU where a 2nd picture start code wasn't encountered.
        foreach (var vobu in plan.Vobus)
        {
            if (vobu.FirstRefEndOffset == 0 && vobu.SectorCount > 1)
                vobu.FirstRefEndOffset = vobu.SectorCount - 1;
        }

        return lba;
    }

    private static void InspectNonNavPack(
        ReadOnlySpan<byte> pack,
        VobuRecord vobu,
        uint relOffset,
        ref int pictureCountInVobu)
    {
        if (pack[0] != 0x00 || pack[1] != 0x00 || pack[2] != 0x01 || pack[3] != 0xBA)
            return;

        int pos = 14 + (pack[13] & 0x07);
        if (pos + 6 > SectorSize)
            return;

        // Skip optional system header (00 00 01 BB).
        if (pack[pos] == 0x00 && pack[pos + 1] == 0x00 && pack[pos + 2] == 0x01 && pack[pos + 3] == 0xBB)
        {
            int sysLen = BinaryPrimitives.ReadUInt16BigEndian(pack.Slice(pos + 4, 2));
            pos += 6 + sysLen;
            if (pos + 6 > SectorSize)
                return;
        }

        if (pack[pos] != 0x00 || pack[pos + 1] != 0x00 || pack[pos + 2] != 0x01)
            return;

        byte streamId = pack[pos + 3];

        // Video elementary stream (0xE0..0xEF).
        if (streamId >= 0xE0 && streamId <= 0xEF)
        {
            if (pos + 9 <= SectorSize)
            {
                byte flags2 = pack[pos + 7];
                int hdrDataLen = pack[pos + 8];
                if ((flags2 & 0x80) != 0 && pos + 14 <= SectorSize)
                {
                    ulong pts = ReadPesPts(pack.Slice(pos + 9, 5));
                    if (!vobu.MinVideoPts.HasValue || pts < vobu.MinVideoPts.Value)
                        vobu.MinVideoPts = pts;
                    if (!vobu.MaxVideoPts.HasValue || pts > vobu.MaxVideoPts.Value)
                        vobu.MaxVideoPts = pts;
                }

                // Track end of 1st reference picture (I-frame) for DSI vobu_1stref_ea.
                if (pictureCountInVobu < 2)
                {
                    int payloadStart = pos + 9 + hdrDataLen;
                    int pesLen = BinaryPrimitives.ReadUInt16BigEndian(pack.Slice(pos + 4, 2));
                    int payloadEnd = pesLen > 0 ? Math.Min(SectorSize, pos + 6 + pesLen) : SectorSize;

                    for (int i = payloadStart; i + 3 < payloadEnd; i++)
                    {
                        if (pack[i] == 0x00 && pack[i + 1] == 0x00 && pack[i + 2] == 0x01 && pack[i + 3] == 0x00)
                        {
                            pictureCountInVobu++;
                            if (pictureCountInVobu == 2)
                            {
                                vobu.FirstRefEndOffset = relOffset;
                                break;
                            }
                        }
                    }

                    if (pictureCountInVobu == 1)
                        vobu.FirstRefEndOffset = relOffset;
                }
            }
        }
        // Private stream 1 (AC-3 0x80..0x87, DTS 0x88..0x8F, LPCM 0xA0..0xA7).
        else if (streamId == 0xBD && pos + 9 <= SectorSize)
        {
            int hdrDataLen = pack[pos + 8];
            int subIdPos = pos + 9 + hdrDataLen;
            if (subIdPos < SectorSize)
            {
                byte subId = pack[subIdPos];
                int audioIndex = -1;
                if (subId >= 0x80 && subId <= 0x87) audioIndex = subId - 0x80;
                else if (subId >= 0x88 && subId <= 0x8F) audioIndex = subId - 0x88;
                else if (subId >= 0xA0 && subId <= 0xA7) audioIndex = subId - 0xA0;

                if (audioIndex >= 0 && audioIndex < 8 && vobu.AudioFirstOffset[audioIndex] == 0x7FFF && relOffset <= 0x7FFE)
                    vobu.AudioFirstOffset[audioIndex] = (ushort)relOffset;
            }
        }
        // MPEG audio stream (0xC0..0xC7).
        else if (streamId >= 0xC0 && streamId <= 0xC7)
        {
            int audioIndex = streamId - 0xC0;
            if (vobu.AudioFirstOffset[audioIndex] == 0x7FFF && relOffset <= 0x7FFE)
                vobu.AudioFirstOffset[audioIndex] = (ushort)relOffset;
        }
    }

    private static void FinalizeTitleTimingAndChapters(TitlePlan plan)
    {
        ulong frameTicks = plan.Input.Standard == DvdVideoStandard.Pal ? 3600UL : 3003UL;
        var vobus = plan.Vobus;

        // Resolve StartPtm for every VOBU.
        for (int i = 0; i < vobus.Count; i++)
        {
            var v = vobus[i];
            if (v.MinVideoPts.HasValue)
                v.StartPtm = v.MinVideoPts.Value;
            else if (i > 0)
                v.StartPtm = vobus[i - 1].StartPtm + frameTicks;
            else
                v.StartPtm = v.ScrBase;
        }

        // Resolve EndPtm for every VOBU (strictly > StartPtm).
        for (int i = 0; i < vobus.Count; i++)
        {
            var v = vobus[i];
            if (i + 1 < vobus.Count && vobus[i + 1].StartPtm > v.StartPtm)
            {
                v.EndPtm = vobus[i + 1].StartPtm;
            }
            else
            {
                ulong lastPts = v.MaxVideoPts.HasValue && v.MaxVideoPts.Value >= v.StartPtm
                    ? v.MaxVideoPts.Value
                    : v.StartPtm;
                v.EndPtm = lastPts + frameTicks;
            }
        }

        // Split chapters every 5 minutes (capped at MaxCellsPerTitle = 99).
        ulong titleStartPtm = vobus[0].StartPtm;
        ulong totalPtm = vobus[^1].EndPtm > titleStartPtm ? vobus[^1].EndPtm - titleStartPtm : frameTicks;
        ulong effectiveInterval = ChapterIntervalTicks;
        if (totalPtm / effectiveInterval >= MaxCellsPerTitle)
            effectiveInterval = (totalPtm / (MaxCellsPerTitle - 1)) + 1;

        byte currentCellId = 1;
        int cellStartIdx = 0;
        ulong nextSplitPtm = titleStartPtm + effectiveInterval;

        for (int i = 0; i < vobus.Count; i++)
        {
            if (i > cellStartIdx && currentCellId < MaxCellsPerTitle && vobus[i].StartPtm >= nextSplitPtm)
            {
                CloseCell(plan, currentCellId, cellStartIdx, i - 1);
                currentCellId++;
                cellStartIdx = i;
                nextSplitPtm = vobus[i].StartPtm + effectiveInterval;
            }

            vobus[i].CellId = currentCellId;
            ulong cellStartPtm = vobus[cellStartIdx].StartPtm;
            vobus[i].CellElapsedTicks = vobus[i].StartPtm >= cellStartPtm
                ? vobus[i].StartPtm - cellStartPtm
                : 0;
        }

        CloseCell(plan, currentCellId, cellStartIdx, vobus.Count - 1);
        plan.TotalDurationTicks = plan.Cells.Aggregate(0UL, (acc, c) => acc + c.DurationTicks);
    }

    private static void CloseCell(TitlePlan plan, byte cellId, int firstIdx, int lastIdx)
    {
        var first = plan.Vobus[firstIdx];
        var last = plan.Vobus[lastIdx];
        ulong dur = last.EndPtm > first.StartPtm ? last.EndPtm - first.StartPtm : 3600UL;

        plan.Cells.Add(new CellRecord
        {
            VobId = plan.VobId,
            CellId = cellId,
            FirstVobuIndex = firstIdx,
            LastVobuIndex = lastIdx,
            FirstLba = first.Lba,
            LastVobuLba = last.Lba,
            LastSector = last.Lba + last.SectorCount - 1,
            DurationTicks = dur
        });
    }

    private static void WritePatchedVobFiles(
        List<TitlePlan> plans,
        string videoTsDir,
        long totalInputBytes,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        byte[] buffer = new byte[IoBufferBytes];
        int vobFileIndex = 1;
        int sectorsInCurrentVob = 0;
        long bytesWrittenTotal = 0;

        FileStream? outStream = OpenVobFile(videoTsDir, vobFileIndex);
        try
        {
            foreach (var plan in plans)
            {
                ct.ThrowIfCancellationRequested();
                int vobuCursor = 0;
                int cellCursor = 0;

                using var inStream = new FileStream(plan.Input.MpgPath, FileMode.Open, FileAccess.Read, FileShare.Read, IoBufferBytes, FileOptions.SequentialScan);
                if (plan.SourcePackOffset > 0)
                    inStream.Seek(plan.SourcePackOffset * SectorSize, SeekOrigin.Begin);

                long remainingPacks = plan.SourcePackCount;
                while (remainingPacks > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int packsToRead = (int)Math.Min(IoBufferSectors, remainingPacks);
                    int read = ReadExactBytes(inStream, buffer, packsToRead * SectorSize);
                    int sectorCount = read / SectorSize;
                    if (sectorCount == 0)
                        break;

                    for (int s = 0; s < sectorCount; s++)
                    {
                        Span<byte> pack = buffer.AsSpan(s * SectorSize, SectorSize);
                        if (IsNavPack(pack) && vobuCursor < plan.Vobus.Count)
                        {
                            while (cellCursor + 1 < plan.Cells.Count && vobuCursor > plan.Cells[cellCursor].LastVobuIndex)
                                cellCursor++;

                            PatchNavPack(pack, plan, vobuCursor, plan.Cells[cellCursor]);
                            vobuCursor++;
                        }

                        if (sectorsInCurrentVob >= MaxVobSectorsPerFile)
                        {
                            outStream.Dispose();
                            vobFileIndex++;
                            if (vobFileIndex > 9)
                                throw new InvalidOperationException("Authored DVD-Video exceeds the 9 VOB file limit (9 GiB) for a single titleset.");
                            outStream = OpenVobFile(videoTsDir, vobFileIndex);
                            sectorsInCurrentVob = 0;
                        }

                        outStream.Write(pack);
                        sectorsInCurrentVob++;
                    }

                    remainingPacks -= sectorCount;
                    bytesWrittenTotal += (long)sectorCount * SectorSize;
                    if (progress is not null && totalInputBytes > 0)
                    {
                        double writeFraction = Math.Clamp(bytesWrittenTotal / (double)totalInputBytes, 0.0, 1.0);
                        progress.Report(0.45 + writeFraction * 0.50);
                    }
                }
            }
        }
        finally
        {
            outStream?.Dispose();
        }
    }

    private static FileStream OpenVobFile(string videoTsDir, int index)
    {
        string path = Path.Combine(videoTsDir, $"VTS_01_{index}.VOB");
        return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, IoBufferBytes);
    }

    private static void PatchNavPack(Span<byte> pack, TitlePlan plan, int vobuIndex, CellRecord cell)
    {
        var vobus = plan.Vobus;
        var vobu = vobus[vobuIndex];
        DvdVideoStandard standard = plan.Input.Standard;

        // Zero out PCI and DSI data payloads before populating (preserving PES headers and substream IDs).
        pack.Slice(PciDataOffset, 979).Clear();
        pack.Slice(DsiDataOffset, 1017).Clear();

        // --- PCI General Information (pci_gi, 60 bytes at PciDataOffset) ---
        Span<byte> pci = pack.Slice(PciDataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(pci.Slice(0x00, 4), vobu.Lba);              // nv_pck_lbn
        BinaryPrimitives.WriteUInt16BigEndian(pci.Slice(0x04, 2), 0x0000);                // vobu_cat
        BinaryPrimitives.WriteUInt16BigEndian(pci.Slice(0x06, 2), 0x0000);                // reserved
        BinaryPrimitives.WriteUInt32BigEndian(pci.Slice(0x08, 4), 0x0000_0000);           // vobu_uop_ctl (all allowed)
        BinaryPrimitives.WriteUInt32BigEndian(pci.Slice(0x0C, 4), (uint)vobu.StartPtm);   // vobu_s_ptm
        BinaryPrimitives.WriteUInt32BigEndian(pci.Slice(0x10, 4), (uint)vobu.EndPtm);     // vobu_e_ptm
        BinaryPrimitives.WriteUInt32BigEndian(pci.Slice(0x14, 4), 0x0000_0000);           // vobu_se_e_ptm
        WriteDvdTime(pci.Slice(0x18, 4), vobu.CellElapsedTicks, standard);                // c_eltm

        // --- DSI General Information (dsi_gi, 32 bytes at DsiDataOffset) ---
        Span<byte> dsi = pack.Slice(DsiDataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(dsi.Slice(0x00, 4), (uint)(vobu.ScrBase & 0xFFFF_FFFFUL)); // nv_pck_scr
        BinaryPrimitives.WriteUInt32BigEndian(dsi.Slice(0x04, 4), vobu.Lba);                             // nv_pck_lbn
        BinaryPrimitives.WriteUInt32BigEndian(dsi.Slice(0x08, 4), vobu.SectorCount - 1);                 // vobu_ea
        BinaryPrimitives.WriteUInt32BigEndian(dsi.Slice(0x0C, 4), vobu.FirstRefEndOffset);               // vobu_1stref_ea
        BinaryPrimitives.WriteUInt32BigEndian(dsi.Slice(0x10, 4), 0);                                    // vobu_2ndref_ea
        BinaryPrimitives.WriteUInt32BigEndian(dsi.Slice(0x14, 4), 0);                                    // vobu_3rdref_ea
        BinaryPrimitives.WriteUInt16BigEndian(dsi.Slice(0x18, 2), vobu.VobId);                           // vobu_vob_idn
        dsi[0x1A] = 0x00;                                                                                // zero_1
        dsi[0x1B] = vobu.CellId;                                                                         // vobu_c_idn
        WriteDvdTime(dsi.Slice(0x1C, 4), vobu.CellElapsedTicks, standard);                               // c_eltm

        // --- DSI VOBU Search Information (vobu_sri, 168 bytes at DsiVobuSriOffset) ---
        Span<byte> sri = pack.Slice(DsiVobuSriOffset, 168);

        // next_video (+0) & next_vobu (+80)
        if (vobuIndex < cell.LastVobuIndex)
        {
            uint delta = vobus[vobuIndex + 1].Lba - vobu.Lba;
            BinaryPrimitives.WriteUInt32BigEndian(sri.Slice(0, 4), SriValidFlag | delta);
            BinaryPrimitives.WriteUInt32BigEndian(sri.Slice(80, 4), SriValidFlag | delta);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(sri.Slice(0, 4), SriNoVideo);
            BinaryPrimitives.WriteUInt32BigEndian(sri.Slice(80, 4), SriEndOfCell);
        }

        // fwda[0..18] (+4..+79)
        for (int k = 0; k < 19; k++)
        {
            ulong targetPtm = vobu.StartPtm + (ulong)(FwdaSeconds[k] * 90_000.0);
            int targetIdx = FindVobuByPtmForward(vobus, vobuIndex, cell.LastVobuIndex, targetPtm);
            uint entry = targetIdx >= 0
                ? SriValidFlag | (vobus[targetIdx].Lba - vobu.Lba)
                : SriEndOfCell;
            BinaryPrimitives.WriteUInt32BigEndian(sri.Slice(4 + k * 4, 4), entry);
        }

        // prev_vobu (+84) & prev_video (+164)
        if (vobuIndex > cell.FirstVobuIndex)
        {
            uint delta = vobu.Lba - vobus[vobuIndex - 1].Lba;
            BinaryPrimitives.WriteUInt32BigEndian(sri.Slice(84, 4), SriValidFlag | delta);
            BinaryPrimitives.WriteUInt32BigEndian(sri.Slice(164, 4), SriValidFlag | delta);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(sri.Slice(84, 4), SriEndOfCell);
            BinaryPrimitives.WriteUInt32BigEndian(sri.Slice(164, 4), SriNoVideo);
        }

        // bwda[0..18] (+88..+163)
        for (int k = 0; k < 19; k++)
        {
            ulong offsetTicks = (ulong)(BwdaSeconds[k] * 90_000.0);
            int targetIdx = vobu.StartPtm >= offsetTicks
                ? FindVobuByPtmBackward(vobus, cell.FirstVobuIndex, vobuIndex, vobu.StartPtm - offsetTicks)
                : -1;
            uint entry = targetIdx >= 0
                ? SriValidFlag | (vobu.Lba - vobus[targetIdx].Lba)
                : SriEndOfCell;
            BinaryPrimitives.WriteUInt32BigEndian(sri.Slice(88 + k * 4, 4), entry);
        }

        // --- DSI Synchronous Information (synci, 144 bytes at DsiSynciOffset) ---
        Span<byte> synci = pack.Slice(DsiSynciOffset, 144);
        for (int a = 0; a < 8; a++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(synci.Slice(a * 2, 2), vobu.AudioFirstOffset[a]);
        }
    }

    private static int FindVobuByPtmForward(List<VobuRecord> vobus, int fromIdx, int cellLastIdx, ulong targetPtm)
    {
        if (fromIdx >= cellLastIdx || vobus[cellLastIdx].StartPtm < targetPtm)
            return -1;

        int lo = fromIdx + 1;
        int hi = cellLastIdx;
        int result = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (vobus[mid].StartPtm >= targetPtm)
            {
                result = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return result;
    }

    private static int FindVobuByPtmBackward(List<VobuRecord> vobus, int cellFirstIdx, int fromIdx, ulong targetPtm)
    {
        if (fromIdx <= cellFirstIdx || vobus[cellFirstIdx].StartPtm > targetPtm)
            return -1;

        int lo = cellFirstIdx;
        int hi = fromIdx - 1;
        int result = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (vobus[mid].StartPtm <= targetPtm)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }

    // =========================================================================
    // VTS_01_0.IFO Generation
    // =========================================================================

    private static byte[] BuildVtsIfo(
        List<TitlePlan> plans,
        uint totalVobSectors,
        DvdVideoStandard standard,
        DvdAspectRatio aspectRatio,
        int audioStreamCount,
        out byte[] vtsMatSector)
    {
        byte[] pttSrpt = PadToSector(BuildVtsPttSrpt(plans));
        byte[] pgcit = PadToSector(BuildVtsPgcit(plans, standard, audioStreamCount));
        byte[] tmapt = PadToSector(BuildVtsTmapt(plans));
        byte[] cAdt = PadToSector(BuildVtsCAdt(plans));
        byte[] vobuAdmap = PadToSector(BuildVtsVobuAdmap(plans));

        uint pttSector = 1;
        uint pgcitSector = pttSector + (uint)(pttSrpt.Length / SectorSize);
        uint tmaptSector = pgcitSector + (uint)(pgcit.Length / SectorSize);
        uint cAdtSector = tmaptSector + (uint)(tmapt.Length / SectorSize);
        uint vobuAdmapSector = cAdtSector + (uint)(cAdt.Length / SectorSize);
        uint ifoSectors = vobuAdmapSector + (uint)(vobuAdmap.Length / SectorSize);

        vtsMatSector = new byte[SectorSize];
        Span<byte> mat = vtsMatSector;

        Encoding.ASCII.GetBytes("DVDVIDEO-VTS", mat.Slice(0x000, 12));
        uint vtsLastSector = ifoSectors + totalVobSectors + ifoSectors - 1;
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x00C, 4), vtsLastSector);     // vts_last_sector
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x01C, 4), ifoSectors - 1);    // vtsi_last_sector
        mat[0x020] = 0x00;
        mat[0x021] = 0x11;                                                              // specification_version 1.1
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x022, 4), 0x0000_0000);       // vts_category
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x080, 4), SectorSize - 1);    // vtsi_last_byte
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0C0, 4), 0);                 // vtsm_vobs (none)
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0C4, 4), ifoSectors);        // vtstt_vobs
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0C8, 4), pttSector);         // vts_ptt_srpt
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0CC, 4), pgcitSector);       // vts_pgcit
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0D0, 4), 0);                 // vtsm_pgci_ut
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0D4, 4), tmaptSector);       // vts_tmapt
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0D8, 4), 0);                 // vtsm_c_adt
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0DC, 4), 0);                 // vtsm_vobu_admap
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0E0, 4), cAdtSector);        // vts_c_adt
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0E4, 4), vobuAdmapSector);   // vts_vobu_admap

        // Video attributes (0x200): MPEG-2 (01), PAL/NTSC, 16:9/4:3, 720x576/480.
        ushort videoAttr = BuildVideoAttr(standard, aspectRatio);
        BinaryPrimitives.WriteUInt16BigEndian(mat.Slice(0x200, 2), videoAttr);

        // Audio streams (0x202 count, 0x204..0x243 attributes: AC-3, 48 kHz, 16-bit, stereo).
        BinaryPrimitives.WriteUInt16BigEndian(mat.Slice(0x202, 2), (ushort)audioStreamCount);
        for (int a = 0; a < audioStreamCount; a++)
        {
            Span<byte> attr = mat.Slice(0x204 + a * 8, 8);
            attr[0] = 0x00; // AC-3 (000), no multichannel ext, unspecified lang (00), unspecified app (00)
            attr[1] = 0x01; // 16-bit (00), 48 kHz (00), 2 channels (001)
        }

        // Subpicture streams = 0.
        BinaryPrimitives.WriteUInt16BigEndian(mat.Slice(0x254, 2), 0);

        byte[] result = new byte[ifoSectors * SectorSize];
        vtsMatSector.CopyTo(result, 0);
        pttSrpt.CopyTo(result, (int)(pttSector * SectorSize));
        pgcit.CopyTo(result, (int)(pgcitSector * SectorSize));
        tmapt.CopyTo(result, (int)(tmaptSector * SectorSize));
        cAdt.CopyTo(result, (int)(cAdtSector * SectorSize));
        vobuAdmap.CopyTo(result, (int)(vobuAdmapSector * SectorSize));
        return result;
    }

    private static ushort BuildVideoAttr(DvdVideoStandard standard, DvdAspectRatio aspectRatio)
    {
        ushort attr = 0x4000; // MPEG-2 (bits 15..14 = 01)
        if (standard == DvdVideoStandard.Pal)
            attr |= 0x1000;   // PAL (bits 13..12 = 01); NTSC = 00
        if (aspectRatio != DvdAspectRatio.Standard4x3)
        {
            attr |= 0x0C00;   // 16:9 (bits 11..10 = 11)
            attr |= 0x0200;   // auto pan-scan prohibited, letterbox permitted (bits 9..8 = 10)
        }
        return attr;
    }

    private static byte[] BuildVtsPttSrpt(List<TitlePlan> plans)
    {
        int titleCount = plans.Count;
        int totalPtts = plans.Sum(p => p.Cells.Count);
        int headerSize = 8 + titleCount * 4;
        int totalSize = headerSize + totalPtts * 4;
        byte[] buf = new byte[totalSize];

        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), (ushort)titleCount);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), (uint)(totalSize - 1));

        int writePos = headerSize;
        for (int t = 0; t < titleCount; t++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8 + t * 4, 4), (uint)writePos);
            int cellCount = plans[t].Cells.Count;
            for (int c = 0; c < cellCount; c++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(writePos, 2), (ushort)(t + 1));     // pgcn
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(writePos + 2, 2), (ushort)(c + 1)); // pgn
                writePos += 4;
            }
        }

        return buf;
    }

    private static byte[] BuildVtsPgcit(List<TitlePlan> plans, DvdVideoStandard standard, int audioStreamCount)
    {
        int titleCount = plans.Count;
        var pgcBlobs = new List<byte[]>(titleCount);

        for (int t = 0; t < titleCount; t++)
        {
            pgcBlobs.Add(BuildTitlePgc(plans[t], t, titleCount, standard, audioStreamCount));
        }

        int headerSize = 8 + titleCount * 8;
        int totalSize = headerSize + pgcBlobs.Sum(b => b.Length);
        byte[] buf = new byte[totalSize];

        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), (ushort)titleCount);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), (uint)(totalSize - 1));

        int writePos = headerSize;
        for (int t = 0; t < titleCount; t++)
        {
            int srpOff = 8 + t * 8;
            buf[srpOff] = (byte)(0x80 | (t + 1)); // entry_id: entry PGC for title t+1
            buf[srpOff + 1] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(srpOff + 2, 2), 0x0000);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(srpOff + 4, 4), (uint)writePos);

            pgcBlobs[t].CopyTo(buf, writePos);
            writePos += pgcBlobs[t].Length;
        }

        return buf;
    }

    private static byte[] BuildTitlePgc(
        TitlePlan plan,
        int titleIndex,
        int totalTitles,
        DvdVideoStandard standard,
        int audioStreamCount)
    {
        int cellCount = plan.Cells.Count;

        const int pgcHeaderSize = 0xEC; // 236 bytes
        const int cmdTblSize = 16;      // 8-byte header + 1 post-command (8 bytes)
        int progMapSize = (cellCount + 1) & ~1; // align to 2-byte boundary
        int cellPlaybackSize = cellCount * 24;
        int cellPositionSize = cellCount * 4;

        int cmdTblOffset = pgcHeaderSize;
        int progMapOffset = cmdTblOffset + cmdTblSize;
        int cellPlaybackOffset = progMapOffset + progMapSize;
        int cellPositionOffset = cellPlaybackOffset + cellPlaybackSize;
        int totalSize = cellPositionOffset + cellPositionSize;

        byte[] buf = new byte[totalSize];
        Span<byte> pgc = buf;

        pgc[0x02] = (byte)cellCount; // nr_of_programs
        pgc[0x03] = (byte)cellCount; // nr_of_cells
        WriteDvdTime(pgc.Slice(0x04, 4), plan.TotalDurationTicks, standard);
        BinaryPrimitives.WriteUInt32BigEndian(pgc.Slice(0x08, 4), 0x0000_0000); // prohibited_ops

        // audio_control[0..7] at 0x0C
        for (int a = 0; a < audioStreamCount; a++)
        {
            ushort ctl = (ushort)(0x8000 | (a << 8));
            BinaryPrimitives.WriteUInt16BigEndian(pgc.Slice(0x0C + a * 2, 2), ctl);
        }

        // palette[0..15] at 0xA4
        for (int i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(pgc.Slice(0xA4 + i * 4, 4), DefaultPgcPalette[i]);
        }

        BinaryPrimitives.WriteUInt16BigEndian(pgc.Slice(0xE4, 2), (ushort)cmdTblOffset);
        BinaryPrimitives.WriteUInt16BigEndian(pgc.Slice(0xE6, 2), (ushort)progMapOffset);
        BinaryPrimitives.WriteUInt16BigEndian(pgc.Slice(0xE8, 2), (ushort)cellPlaybackOffset);
        BinaryPrimitives.WriteUInt16BigEndian(pgc.Slice(0xEA, 2), (ushort)cellPositionOffset);

        // Command table: 1 post-command (JumpVTS_TT next title, or Exit on last title).
        Span<byte> cmd = pgc.Slice(cmdTblOffset, cmdTblSize);
        BinaryPrimitives.WriteUInt16BigEndian(cmd.Slice(0, 2), 0);  // nr_of_pre
        BinaryPrimitives.WriteUInt16BigEndian(cmd.Slice(2, 2), 1);  // nr_of_post
        BinaryPrimitives.WriteUInt16BigEndian(cmd.Slice(4, 2), 0);  // nr_of_cell
        BinaryPrimitives.WriteUInt16BigEndian(cmd.Slice(6, 2), 15); // last_byte
        if (titleIndex + 1 < totalTitles)
        {
            // JumpVTS_TT (titleIndex + 2)
            cmd[8] = 0x30;
            cmd[9] = 0x03;
            cmd[13] = (byte)(titleIndex + 2);
        }
        else
        {
            // Exit
            cmd[8] = 0x30;
            cmd[9] = 0x01;
        }

        // Program map (1 program per cell).
        for (int c = 0; c < cellCount; c++)
        {
            pgc[progMapOffset + c] = (byte)(c + 1);
        }

        // Cell playback info (24 bytes per cell).
        for (int c = 0; c < cellCount; c++)
        {
            var cell = plan.Cells[c];
            Span<byte> cp = pgc.Slice(cellPlaybackOffset + c * 24, 24);
            cp[0x00] = c == 0 ? (byte)0x02 : (byte)0x00; // STC discontinuity on first cell
            cp[0x01] = 0x00;
            cp[0x02] = 0x00; // still_time
            cp[0x03] = 0x00; // cell_cmd_nr
            WriteDvdTime(cp.Slice(0x04, 4), cell.DurationTicks, standard);
            BinaryPrimitives.WriteUInt32BigEndian(cp.Slice(0x08, 4), cell.FirstLba);
            BinaryPrimitives.WriteUInt32BigEndian(cp.Slice(0x0C, 4), 0); // first_ilvu_end_sector
            BinaryPrimitives.WriteUInt32BigEndian(cp.Slice(0x10, 4), cell.LastVobuLba);
            BinaryPrimitives.WriteUInt32BigEndian(cp.Slice(0x14, 4), cell.LastSector);
        }

        // Cell position info (4 bytes per cell).
        for (int c = 0; c < cellCount; c++)
        {
            var cell = plan.Cells[c];
            Span<byte> pos = pgc.Slice(cellPositionOffset + c * 4, 4);
            BinaryPrimitives.WriteUInt16BigEndian(pos.Slice(0, 2), cell.VobId);
            pos[2] = 0x00;
            pos[3] = cell.CellId;
        }

        return buf;
    }

    private static byte[] BuildVtsTmapt(List<TitlePlan> plans)
    {
        int titleCount = plans.Count;
        var tmapBlobs = new List<byte[]>(titleCount);

        foreach (var plan in plans)
        {
            ulong totalSec = plan.TotalDurationTicks / 90_000UL;
            // Choose tmu (seconds per entry) so entry count stays <= 2048.
            byte tmu = (byte)Math.Clamp((int)(totalSec / 2048UL) + 1, 1, 255);
            int entryCount = (int)(totalSec / tmu);

            byte[] tmap = new byte[4 + entryCount * 4];
            tmap[0] = tmu;
            tmap[1] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(tmap.AsSpan(2, 2), (ushort)entryCount);

            ulong startPtm = plan.Vobus[0].StartPtm;
            int vIdx = 0;
            for (int e = 0; e < entryCount; e++)
            {
                ulong targetPtm = startPtm + (ulong)(e + 1) * tmu * 90_000UL;
                while (vIdx + 1 < plan.Vobus.Count && plan.Vobus[vIdx + 1].StartPtm <= targetPtm)
                    vIdx++;
                BinaryPrimitives.WriteUInt32BigEndian(tmap.AsSpan(4 + e * 4, 4), plan.Vobus[vIdx].Lba);
            }

            tmapBlobs.Add(tmap);
        }

        int headerSize = 8 + titleCount * 4;
        int totalSize = headerSize + tmapBlobs.Sum(b => b.Length);
        byte[] buf = new byte[totalSize];

        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), (ushort)titleCount);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), (uint)(totalSize - 1));

        int writePos = headerSize;
        for (int t = 0; t < titleCount; t++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8 + t * 4, 4), (uint)writePos);
            tmapBlobs[t].CopyTo(buf, writePos);
            writePos += tmapBlobs[t].Length;
        }

        return buf;
    }

    private static byte[] BuildVtsCAdt(List<TitlePlan> plans)
    {
        int totalCells = plans.Sum(p => p.Cells.Count);
        int totalSize = 8 + totalCells * 12;
        byte[] buf = new byte[totalSize];

        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), (ushort)plans.Count); // nr_of_vobs
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), (uint)(totalSize - 1));

        int pos = 8;
        foreach (var plan in plans)
        {
            foreach (var cell in plan.Cells)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(pos, 2), cell.VobId);
                buf[pos + 2] = cell.CellId;
                buf[pos + 3] = 0x00;
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos + 4, 4), cell.FirstLba);
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos + 8, 4), cell.LastSector);
                pos += 12;
            }
        }

        return buf;
    }

    private static byte[] BuildVtsVobuAdmap(List<TitlePlan> plans)
    {
        int totalVobus = plans.Sum(p => p.Vobus.Count);
        int totalSize = 4 + totalVobus * 4;
        byte[] buf = new byte[totalSize];

        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)(totalSize - 1));

        int pos = 4;
        foreach (var plan in plans)
        {
            foreach (var vobu in plan.Vobus)
            {
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos, 4), vobu.Lba);
                pos += 4;
            }
        }

        return buf;
    }

    // =========================================================================
    // VIDEO_TS.IFO Generation
    // =========================================================================

    private static byte[] BuildVmgIfo(List<TitlePlan> plans, string discTitle, byte[] vtsMatSector)
    {
        byte[] ttSrpt = PadToSector(BuildTtSrpt(plans, vmgIfoSectors: 0)); // placeholder sector patched below
        byte[] vtsAtrt = PadToSector(BuildVmgVtsAtrt(vtsMatSector));

        uint ttSrptSector = 1;
        uint vtsAtrtSector = ttSrptSector + (uint)(ttSrpt.Length / SectorSize);
        uint vmgIfoSectors = vtsAtrtSector + (uint)(vtsAtrt.Length / SectorSize);

        // Rebuild TT_SRPT with the exact VTS_01_0.IFO sector offset (= 2 * vmgIfoSectors for IFO + BUP).
        ttSrpt = PadToSector(BuildTtSrpt(plans, vmgIfoSectors));

        byte[] matSector = new byte[SectorSize];
        Span<byte> mat = matSector;

        Encoding.ASCII.GetBytes("DVDVIDEO-VMG", mat.Slice(0x000, 12));
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x00C, 4), vmgIfoSectors * 2 - 1); // vmg_last_sector
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x01C, 4), vmgIfoSectors - 1);     // vmgi_last_sector
        mat[0x020] = 0x00;
        mat[0x021] = 0x11;                                                                  // specification_version 1.1
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x022, 4), 0x0000_0000);           // vmg_category (region-free)
        BinaryPrimitives.WriteUInt16BigEndian(mat.Slice(0x026, 2), 1);                     // vmg_nr_of_volumes
        BinaryPrimitives.WriteUInt16BigEndian(mat.Slice(0x028, 2), 1);                     // vmg_this_volume_nr
        mat[0x02A] = 1;                                                                     // disc_side
        BinaryPrimitives.WriteUInt16BigEndian(mat.Slice(0x03E, 2), 1);                     // vmg_nr_of_title_sets

        // Provider identifier (32 bytes ASCII at 0x040).
        string safeProvider = string.IsNullOrWhiteSpace(discTitle) ? "CDBURNER10" : discTitle.Trim();
        byte[] providerBytes = Encoding.ASCII.GetBytes(safeProvider);
        int copyLen = Math.Min(32, providerBytes.Length);
        providerBytes.AsSpan(0, copyLen).CopyTo(mat.Slice(0x040, 32));

        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x080, 4), SectorSize - 1);        // vmgi_last_byte
        const uint firstPlayPgcOffset = 0x0400;
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x084, 4), firstPlayPgcOffset);    // first_play_pgc
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0C0, 4), 0);                     // vmgm_vobs
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0C4, 4), ttSrptSector);          // tt_srpt
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0C8, 4), 0);                     // vmgm_pgci_ut
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0CC, 4), 0);                     // ptl_mait
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0D0, 4), vtsAtrtSector);         // vts_atrt
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0D4, 4), 0);                     // txtdt_mgi
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0D8, 4), 0);                     // vmgm_c_adt
        BinaryPrimitives.WriteUInt32BigEndian(mat.Slice(0x0DC, 4), 0);                     // vmgm_vobu_admap

        // Mirror VTS video attributes into VMGM video attr (0x100) for players checking PAL/NTSC at VMG level.
        vtsMatSector.AsSpan(0x200, 2).CopyTo(mat.Slice(0x100, 2));

        // First-Play PGC at offset 0x0400 inside sector 0: Pre-command = JumpTT 1.
        Span<byte> fpPgc = mat.Slice((int)firstPlayPgcOffset, 0xEC + 16);
        BinaryPrimitives.WriteUInt16BigEndian(fpPgc.Slice(0xE4, 2), 0x00EC); // command_tbl_offset
        Span<byte> fpCmd = fpPgc.Slice(0xEC, 16);
        BinaryPrimitives.WriteUInt16BigEndian(fpCmd.Slice(0, 2), 1);  // nr_of_pre = 1
        BinaryPrimitives.WriteUInt16BigEndian(fpCmd.Slice(2, 2), 0);  // nr_of_post = 0
        BinaryPrimitives.WriteUInt16BigEndian(fpCmd.Slice(4, 2), 0);  // nr_of_cell = 0
        BinaryPrimitives.WriteUInt16BigEndian(fpCmd.Slice(6, 2), 15); // last_byte
        fpCmd[8] = 0x30;                                               // JumpTT 1
        fpCmd[9] = 0x02;
        fpCmd[13] = 0x01;

        byte[] result = new byte[vmgIfoSectors * SectorSize];
        matSector.CopyTo(result, 0);
        ttSrpt.CopyTo(result, (int)(ttSrptSector * SectorSize));
        vtsAtrt.CopyTo(result, (int)(vtsAtrtSector * SectorSize));
        return result;
    }

    private static byte[] BuildTtSrpt(List<TitlePlan> plans, uint vmgIfoSectors)
    {
        int titleCount = plans.Count;
        int totalSize = 8 + titleCount * 12;
        byte[] buf = new byte[totalSize];

        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), (ushort)titleCount);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), (uint)(totalSize - 1));

        uint vtsStartSector = vmgIfoSectors * 2;
        for (int t = 0; t < titleCount; t++)
        {
            Span<byte> entry = buf.AsSpan(8 + t * 12, 12);
            entry[0] = 0x3C;                                                                   // one-sequential PGC title, PTT/time search allowed
            entry[1] = 1;                                                                      // nr_of_angles
            BinaryPrimitives.WriteUInt16BigEndian(entry.Slice(2, 2), (ushort)plans[t].Cells.Count); // nr_of_ptts
            BinaryPrimitives.WriteUInt16BigEndian(entry.Slice(4, 2), 0x0000);                  // parental_id
            entry[6] = 1;                                                                      // title_set_nr = VTS 1
            entry[7] = (byte)(t + 1);                                                          // vts_ttn
            BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(8, 4), vtsStartSector);          // title_set_sector
        }

        return buf;
    }

    private static byte[] BuildVmgVtsAtrt(byte[] vtsMatSector)
    {
        const int vtsAttrSize = 0x308; // 776 bytes
        const int totalSize = 8 + 4 + vtsAttrSize;
        byte[] buf = new byte[totalSize];

        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), 1);                    // nr_of_vtss = 1
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), (uint)(totalSize - 1));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), 12);                   // offset to vts_attributes[0]

        Span<byte> attr = buf.AsSpan(12, vtsAttrSize);
        BinaryPrimitives.WriteUInt32BigEndian(attr.Slice(0, 4), vtsAttrSize - 1);      // last_byte
        BinaryPrimitives.WriteUInt32BigEndian(attr.Slice(4, 4), 0x0000_0000);          // vts_cat
        // Bytes 0x008..0x307 mirror VTSI_MAT bytes 0x100..0x3FF verbatim.
        vtsMatSector.AsSpan(0x100, 0x300).CopyTo(attr.Slice(0x008, 0x300));

        return buf;
    }

    // =========================================================================
    // Low-level MPEG-2 / DVD-Video Binary Helpers
    // =========================================================================

    private static bool IsNavPack(ReadOnlySpan<byte> pack) =>
        pack[0] == 0x00 && pack[1] == 0x00 && pack[2] == 0x01 && pack[3] == 0xBA &&
        pack[14] == 0x00 && pack[15] == 0x00 && pack[16] == 0x01 && pack[17] == 0xBB &&
        pack[PciPacketOffset] == 0x00 && pack[PciPacketOffset + 1] == 0x00 &&
        pack[PciPacketOffset + 2] == 0x01 && pack[PciPacketOffset + 3] == 0xBF &&
        pack[PciPacketOffset + 6] == 0x00 &&
        pack[DsiPacketOffset] == 0x00 && pack[DsiPacketOffset + 1] == 0x00 &&
        pack[DsiPacketOffset + 2] == 0x01 && pack[DsiPacketOffset + 3] == 0xBF &&
        pack[DsiPacketOffset + 6] == 0x01;

    private static ulong ReadScrBase(ReadOnlySpan<byte> pack)
    {
        ulong b4 = pack[4];
        ulong b5 = pack[5];
        ulong b6 = pack[6];
        ulong b7 = pack[7];
        ulong b8 = pack[8];

        return (((b4 >> 3) & 0x07UL) << 30)
             | ((b4 & 0x03UL) << 28)
             | (b5 << 20)
             | (((b6 >> 3) & 0x1FUL) << 15)
             | ((b6 & 0x03UL) << 13)
             | (b7 << 5)
             | ((b8 >> 3) & 0x1FUL);
    }

    private static ulong ReadPesPts(ReadOnlySpan<byte> p) =>
        (((ulong)p[0] & 0x0EUL) << 29)
      | ((ulong)p[1] << 22)
      | (((ulong)p[2] & 0xFEUL) << 14)
      | ((ulong)p[3] << 7)
      | ((ulong)p[4] >> 1);

    private static void WriteDvdTime(Span<byte> dest, ulong ticks90k, DvdVideoStandard standard)
    {
        int fps = standard == DvdVideoStandard.Pal ? 25 : 30;
        byte fpsFlag = standard == DvdVideoStandard.Pal ? (byte)0x40 : (byte)0xC0;

        long totalFrames = standard == DvdVideoStandard.Pal
            ? (long)Math.Round(ticks90k / 3600.0)
            : (long)Math.Round(ticks90k / 3003.0);

        if (totalFrames < 0) totalFrames = 0;

        int frames = (int)(totalFrames % fps);
        long totalSeconds = totalFrames / fps;
        int seconds = (int)(totalSeconds % 60);
        int minutes = (int)((totalSeconds / 60) % 60);
        int hours = (int)Math.Min(99, totalSeconds / 3600);

        dest[0] = ToBcd(hours);
        dest[1] = ToBcd(minutes);
        dest[2] = ToBcd(seconds);
        dest[3] = (byte)(ToBcd(frames) | fpsFlag);
    }

    private static byte ToBcd(int value)
    {
        int clamped = Math.Clamp(value, 0, 99);
        return (byte)(((clamped / 10) << 4) | (clamped % 10));
    }

    private static byte[] PadToSector(byte[] data)
    {
        int paddedLen = ((data.Length + SectorSize - 1) / SectorSize) * SectorSize;
        if (paddedLen == 0) paddedLen = SectorSize;
        if (paddedLen == data.Length) return data;

        byte[] padded = new byte[paddedLen];
        data.CopyTo(padded, 0);
        return padded;
    }

    private static int ReadExactSectors(Stream stream, byte[] buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int n = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (n == 0)
                break;
            totalRead += n;
        }
        return (totalRead / SectorSize) * SectorSize;
    }

    private static int ReadExactBytes(Stream stream, byte[] buffer, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int n = stream.Read(buffer, totalRead, count - totalRead);
            if (n == 0)
                break;
            totalRead += n;
        }
        return totalRead;
    }
}
