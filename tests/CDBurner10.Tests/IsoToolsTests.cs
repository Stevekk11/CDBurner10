using System.Buffers.Binary;
using System.Text;
using CDBurner10.Core.Iso;

namespace CDBurner10.Tests;

public sealed class IsoToolsTests
{
    [Fact]
    public async Task ConvertBinToIsoAsync_Mode1_2352Sectors_ExtractsExact2048UserData()
    {
        using var tmp = new TempDirectory();
        const int sectorCount = 4;
        byte[] cdSync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

        var binBytes = new byte[sectorCount * 2352];
        var expectedIso = new byte[sectorCount * 2048];

        for (int s = 0; s < sectorCount; s++)
        {
            int binOff = s * 2352;
            cdSync.CopyTo(binBytes, binOff);
            binBytes[binOff + 15] = 1; // Mode 1

            for (int b = 0; b < 2048; b++)
            {
                byte val = (byte)((s * 37 + b) & 0xFF);
                binBytes[binOff + 16 + b] = val;
                expectedIso[s * 2048 + b] = val;
            }
        }

        var binPath = tmp.CreateFile("mode1.bin", binBytes);
        var isoPath = Path.Combine(tmp.Path, "mode1.iso");

        await IsoTools.ConvertToIsoAsync(binPath, isoPath);

        var actualIso = await File.ReadAllBytesAsync(isoPath);
        Assert.Equal(expectedIso.Length, actualIso.Length);
        Assert.Equal(expectedIso, actualIso);
    }

    [Fact]
    public async Task ConvertNrgToIsoAsync_Ner5AndNeroFooters_Strips307200HeaderAndFooter()
    {
        using var tmp = new TempDirectory();
        const int nrgHeaderSize = 307200;
        var payload = new byte[4096];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 13 + 7) & 0xFF);

        // 1. NER5 format (v2, 64-bit offset)
        {
            long footerOffset = nrgHeaderSize + payload.Length;
            using var ms = new MemoryStream();
            ms.Write(new byte[nrgHeaderSize]);
            ms.Write(payload);
            // Footer chunk starts at footerOffset: "DAOX" dummy chunk + "END!" + 0 + "NER5" + 64-bit BE offset
            ms.Write(Encoding.ASCII.GetBytes("DAOX"));
            ms.Write(new byte[8]); // padding
            ms.Write(Encoding.ASCII.GetBytes("END!"));
            ms.Write(new byte[4]);
            ms.Write(Encoding.ASCII.GetBytes("NER5"));
            var offBuf = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(offBuf, footerOffset);
            ms.Write(offBuf);

            var nrgPath = tmp.CreateFile("test_ner5.nrg", ms.ToArray());
            var isoPath = Path.Combine(tmp.Path, "test_ner5.iso");

            Assert.False(IsoTools.IsNrgAudioImage(nrgPath));
            await IsoTools.ConvertToIsoAsync(nrgPath, isoPath);

            var actual = await File.ReadAllBytesAsync(isoPath);
            Assert.Equal(payload, actual);
        }

        // 2. NERO format (v1, 32-bit offset)
        {
            int footerOffset = nrgHeaderSize + payload.Length;
            using var ms = new MemoryStream();
            ms.Write(new byte[nrgHeaderSize]);
            ms.Write(payload);
            ms.Write(Encoding.ASCII.GetBytes("DAOI"));
            ms.Write(new byte[8]);
            ms.Write(Encoding.ASCII.GetBytes("END!"));
            ms.Write(new byte[4]);
            ms.Write(Encoding.ASCII.GetBytes("NERO"));
            var offBuf = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(offBuf, (uint)footerOffset);
            ms.Write(offBuf);

            var nrgPath = tmp.CreateFile("test_nero.nrg", ms.ToArray());
            var isoPath = Path.Combine(tmp.Path, "test_nero.iso");

            Assert.False(IsoTools.IsNrgAudioImage(nrgPath));
            await IsoTools.ConvertToIsoAsync(nrgPath, isoPath);

            var actual = await File.ReadAllBytesAsync(isoPath);
            Assert.Equal(payload, actual);
        }
    }

    [Fact]
    public void IsNrgAudioImage_DetectsCuesAndCuexTags()
    {
        using var tmp = new TempDirectory();

        // NER5 pointing to CUEX tag -> Audio NRG
        using var ms = new MemoryStream();
        ms.Write(new byte[1024]);
        long offset = ms.Position;
        ms.Write(Encoding.ASCII.GetBytes("CUEX"));
        ms.Write(new byte[16]);
        ms.Write(Encoding.ASCII.GetBytes("NER5"));
        var offBuf = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(offBuf, offset);
        ms.Write(offBuf);

        var audioNrg = tmp.CreateFile("audio_cuex.nrg", ms.ToArray());
        Assert.True(IsoTools.IsNrgAudioImage(audioNrg));

        // NERO pointing to CUES tag -> Audio NRG
        using var ms2 = new MemoryStream();
        ms2.Write(new byte[512]);
        uint offset2 = (uint)ms2.Position;
        ms2.Write(Encoding.ASCII.GetBytes("CUES"));
        ms2.Write(new byte[16]);
        ms2.Write(Encoding.ASCII.GetBytes("NERO"));
        var offBuf2 = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(offBuf2, offset2);
        ms2.Write(offBuf2);

        var audioNrg2 = tmp.CreateFile("audio_cues.nrg", ms2.ToArray());
        Assert.True(IsoTools.IsNrgAudioImage(audioNrg2));
    }

    [Fact]
    public async Task ConvertB5iToIsoAsync_Skips150SectorPregapAndExtracts2048UserData()
    {
        using var tmp = new TempDirectory();
        const int pregapSectors = 150;
        const int dataSectors = 3;
        const int totalSectors = pregapSectors + dataSectors;
        const int sectorSize = 2352;

        var b5i = new byte[totalSectors * sectorSize];
        // Put 12 bytes of 0xFF at offset 2352 so ConvertB5iToIsoAsync detects 2352-byte sector layout
        for (int i = 0; i < 12; i++) b5i[2352 + i] = 0xFF;

        var expected = new byte[dataSectors * 2048];
        for (int d = 0; d < dataSectors; d++)
        {
            int secIdx = pregapSectors + d;
            int off = secIdx * sectorSize + 16;
            for (int b = 0; b < 2048; b++)
            {
                byte val = (byte)((d * 53 + b) & 0xFF);
                b5i[off + b] = val;
                expected[d * 2048 + b] = val;
            }
        }

        var b5iPath = tmp.CreateFile("image.b5i", b5i);
        var isoPath = Path.Combine(tmp.Path, "image.iso");

        await IsoTools.ConvertToIsoAsync(b5iPath, isoPath);

        var actual = await File.ReadAllBytesAsync(isoPath);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);
    }
}
