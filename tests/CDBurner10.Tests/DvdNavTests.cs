using CDBurner10.Core.Burning;
using CDBurner10.Core.Dvd;
using CDBurner10.Core.Native;

namespace CDBurner10.Tests;

public sealed class DvdNavTests
{
    [Fact]
    public void DvdNavInterop_LoadsNativeLibraryAndReturnsVersion()
    {
        Assert.True(DvdNavInterop.IsAvailable);
        Assert.True(DvdNavigationWalker.IsAvailable);

        string version = DvdNavInterop.GetVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Theory]
    [InlineData("D", "D:")]
    [InlineData("d:", "D:")]
    [InlineData(@"D:\", "D:")]
    [InlineData(@"\\.\d:", "D:")]
    public void NormalizeOpenPath_NormalizesWindowsOpticalDriveLetters(string input, string expected)
    {
        Assert.Equal(expected, DvdNavigationWalker.NormalizeOpenPath(input));
    }

    [Fact]
    public void SelectMainTitle_PicksLongestDurationThenMostParts()
    {
        var titles = new[]
        {
            new DvdTitleInfo(1, 1, TimeSpan.FromSeconds(15)),
            new DvdTitleInfo(2, 18, TimeSpan.FromMinutes(112)),
            new DvdTitleInfo(3, 4, TimeSpan.FromMinutes(22)),
            new DvdTitleInfo(4, 24, TimeSpan.FromMinutes(112))
        };

        var main = DvdNavigationWalker.SelectMainTitle(titles);
        Assert.Equal(4, main.TitleNumber);
    }

    [Fact]
    public void BuildFfmpegArguments_AppliesDisableTrimAndPreWalkedStreamFlags()
    {
        var info = new DvdVideoInfo(
            TimeSpan.FromMinutes(90),
            720,
            576,
            [new DvdAudioTrack(1, 0, "ac3", 2, "eng", null)],
            [])
        {
            DisableTrim = true
        };
        var options = new DvdMp4Options { Encoder = DvdVideoEncoder.Software };

        var trimArgs = DvdVideoConverter.BuildFfmpegArguments("E:", @"C:\out.mp4", info, options, "libx264", isPreWalkedMpegStream: false);
        Assert.Contains("-trim", trimArgs);
        Assert.Equal("0", trimArgs[trimArgs.IndexOf("-trim") + 1]);

        var preWalkedArgs = DvdVideoConverter.BuildFfmpegArguments(@"C:\temp.mpg", @"C:\out.mp4", info, options, "libx264", isPreWalkedMpegStream: true);
        Assert.DoesNotContain("-f", preWalkedArgs);
        Assert.DoesNotContain("-trim", preWalkedArgs);
        Assert.Equal(@"C:\temp.mpg", preWalkedArgs[preWalkedArgs.IndexOf("-i") + 1]);
    }
}
