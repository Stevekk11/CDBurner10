using CDBurner10.Core.Burning;
using RocketDivision.StarBurnX;

namespace CDBurner10.Tests;

public sealed class DvdCssTests
{
    [Fact]
    public void EmptyDiscInfo_IsNotCssProtected()
    {
        Assert.False(DiscInfoModel.Empty.IsCssProtected);
    }

    [Fact]
    public void DiscInfoModel_ReportsCssProtection()
    {
        var info = new DiscInfoModel(
            STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_ROM,
            "DVD-ROM",
            "TEST",
            0, 0, 0,
            false, false, false, false, true,
            0,
            Array.Empty<SessionModel>())
        {
            ProtectionSystem = STARBURN_DISC_PROTECTION_SYSTEM.STARBURN_DVD_PROTECTION_CSS
        };

        Assert.True(info.IsCssProtected);
    }

    [Fact]
    public void GrabOptions_DecryptCssByDefault()
    {
        var options = new GrabOptions
        {
            Drive = null!,
            TargetPath = "disc.iso"
        };

        Assert.True(options.DecryptCss);
    }

    [Fact]
    public void DvdCssReader_IsBundledWithTheApplication()
    {
        Assert.True(DvdCssReader.IsAvailable);
        Assert.Equal("libdvdcss-2-32bit.dll", DvdCssReader.DllName);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2048, 1)]
    [InlineData(2049, 2)]
    [InlineData(3_477_000_192, 1_697_754)]
    public void DvdCssReader_ComputesSectorCount(long bytes, long expectedSectors)
    {
        Assert.Equal(expectedSectors, DvdCssReader.GetSectorCount(bytes));
    }
}
