using System.Buffers.Binary;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Dvd;
using CDBurner10.Core.Native;

namespace CDBurner10.Tests;

public sealed class DiscGrabberResilienceTests
{
    [Fact]
    public void ResolveReadMode_IsoTarget_NeverUsesNavigatedModeEvenForDvdVideo()
    {
        var opts = new GrabOptions
        {
            Drive = null!,
            TargetPath = @"C:\Temp\movie.iso",
            IsDvdVideo = true,
            ReadMode = null
        };

        Assert.Equal(DiscReadMode.Linear, DiscGrabber.ResolveReadMode(opts));
    }

    [Fact]
    public void ResolveReadMode_MpgTargetWithDvdVideo_UsesNavigatedModeWhenAvailable()
    {
        var opts = new GrabOptions
        {
            Drive = null!,
            TargetPath = @"C:\Temp\movie.mpg",
            IsDvdVideo = true,
            ReadMode = null
        };

        var expected = DvdNavigationWalker.IsAvailable ? DiscReadMode.Navigated : DiscReadMode.Linear;
        Assert.Equal(expected, DiscGrabber.ResolveReadMode(opts));
    }

    [Fact]
    public void ResolveReadMode_ExplicitLinear_OverridesDvdVideoFlag()
    {
        var opts = new GrabOptions
        {
            Drive = null!,
            TargetPath = @"C:\Temp\movie.mpg",
            IsDvdVideo = true,
            ReadMode = DiscReadMode.Linear
        };

        Assert.Equal(DiscReadMode.Linear, DiscGrabber.ResolveReadMode(opts));
    }

    [Fact]
    public void ResolveReadMode_ExplicitNavigated_ReturnsNavigated()
    {
        var opts = new GrabOptions
        {
            Drive = null!,
            TargetPath = @"C:\Temp\movie.mpg",
            IsDvdVideo = false,
            ReadMode = DiscReadMode.Navigated
        };

        Assert.Equal(DiscReadMode.Navigated, DiscGrabber.ResolveReadMode(opts));
    }

    [Theory]
    [InlineData(-2, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    [InlineData(20, 21)]
    public void GetMaxReadAttempts_ComputesOnePlusRetries(int retries, int expectedAttempts)
    {
        Assert.Equal(expectedAttempts, ScsiOpticalDevice.GetMaxReadAttempts(retries));
    }

    [Fact]
    public void BadSectorTracker_CoalescesContiguousLbaRangesAndFormatsSummary()
    {
        var tracker = new BadSectorTracker();
        tracker.RecordBadSector(100);
        tracker.RecordBadSector(101);
        tracker.RecordBadSector(102);
        tracker.RecordBadSector(205);
        tracker.RecordBadSector(300);
        tracker.RecordBadSector(301);

        Assert.Equal(6, tracker.BadSectorCount);
        Assert.Equal(3, tracker.Ranges.Count);
        Assert.Equal(new LbaRange(100, 102), tracker.Ranges[0]);
        Assert.Equal(3, tracker.Ranges[0].SectorCount);
        Assert.Equal(new LbaRange(205, 205), tracker.Ranges[1]);
        Assert.Equal(new LbaRange(300, 301), tracker.Ranges[2]);
        Assert.Equal("LBA 100..102 (3), LBA 205, LBA 300..301 (2)", tracker.FormatSummary());
    }

    [Fact]
    public void BuildDisabledEcModeSelectBuffer_ParsesModePage01AndPreservesRestoreState()
    {
        // Construct a realistic MODE SENSE (10) response:
        // 8-byte Mode Parameter Header + 0-byte block descriptor + 12-byte Mode Page 0x01
        var sense = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(sense.AsSpan(0, 2), 18); // Mode Data Length = 18 (total 20 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(sense.AsSpan(6, 2), 0);  // Block Descriptor Length = 0

        sense[8] = 0x81;  // PS=1 (0x80) | Page Code = 0x01
        sense[9] = 0x0A;  // Page Length = 10 bytes
        sense[10] = 0xC0; // AWRE (0x80) | ARRE (0x40)
        sense[11] = 0x08; // Read Retry Count = 8

        var v0 = ScsiOpticalDevice.BuildDisabledEcModeSelectBuffer(sense, variant: 0, out var savedOriginal);
        Assert.NotNull(v0);
        Assert.NotNull(savedOriginal);
        Assert.Equal(20, v0.Length);

        // Saved original must have PS bit cleared (0x01) and preserve original flags (0xC0) & retries (8)
        Assert.Equal(0x01, savedOriginal[8]);
        Assert.Equal(0x0A, savedOriginal[9]);
        Assert.Equal(0xC0, savedOriginal[10]);
        Assert.Equal(0x08, savedOriginal[11]);

        // Variant 0: AWRE preserved (0x80) | TB (0x20) | DCR (0x01) = 0xA1, Read Retry Count = 0
        Assert.Equal(0x01, v0[8]);
        Assert.Equal(0xA1, v0[10]);
        Assert.Equal(0x00, v0[11]);

        // Variant 1: AWRE (0x80) | TB (0x20) = 0xA0, Read Retry Count = 0
        var v1 = ScsiOpticalDevice.BuildDisabledEcModeSelectBuffer(sense, variant: 1, out _);
        Assert.NotNull(v1);
        Assert.Equal(0xA0, v1[10]);
        Assert.Equal(0x00, v1[11]);

        // Variant 2: Read Retry Count = 0, ARRE cleared
        var v2 = ScsiOpticalDevice.BuildDisabledEcModeSelectBuffer(sense, variant: 2, out _);
        Assert.NotNull(v2);
        Assert.Equal(0x80, v2[10]);
        Assert.Equal(0x00, v2[11]);
    }

    [Theory]
    [InlineData("movie.mpg", true)]
    [InlineData(@"C:\ripped\Title1.MPEG", true)]
    [InlineData("raw.m2p", true)]
    [InlineData("disc.iso", false)]
    [InlineData("video.mp4", false)]
    [InlineData("", false)]
    public void IsMpegStreamFile_RecognizesMpegProgramStreams(string path, bool expected)
    {
        Assert.Equal(expected, DvdVideoConverter.IsMpegStreamFile(path));
    }
}
