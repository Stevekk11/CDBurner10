using CDBurner10.Core.Printing;

namespace CDBurner10.Tests;

public sealed class CoverPrintServiceTests
{
    [Fact]
    public void MmToDips_ConvertsExactMillimetersAt96Dpi()
    {
        // 25.4 mm == 1 inch == 96 DIPs
        Assert.Equal(96.0, CoverPrintService.MmToDips(25.4), precision: 6);
        Assert.Equal(120.0 / 25.4 * 96.0, CoverPrintService.MmToDips(120.0), precision: 6);
    }

    [Fact]
    public void SplitItemsIntoColumns_DistributesSequentiallyAndNumbersItems()
    {
        var job = new CoverPrintJob
        {
            Title = "My Mix",
            Columns = 2,
            ShowNumbers = true
        };
        job.Items.AddRange(["Track A", "Track B", "Track C", "Track D", "Track E"]);

        var columns = CoverPrintService.SplitItemsIntoColumns(job);

        Assert.Equal(2, columns.Count);
        Assert.Equal(["01. Track A", "02. Track B", "03. Track C"], columns[0]);
        Assert.Equal(["04. Track D", "05. Track E"], columns[1]);
    }

    [Fact]
    public void SplitItemsIntoColumns_CapsColumnsAtItemCountAndRespectsShowNumbersFalse()
    {
        var job = new CoverPrintJob
        {
            Columns = 3,
            ShowNumbers = false
        };
        job.Items.AddRange(["Alpha", "Beta"]);

        var columns = CoverPrintService.SplitItemsIntoColumns(job);

        Assert.Equal(2, columns.Count);
        Assert.Equal(["Alpha"], columns[0]);
        Assert.Equal(["Beta"], columns[1]);
    }

    [Fact]
    public void GetSpineGuideOffsetsMm_ReturnsExpectedFoldLines()
    {
        Assert.Empty(CoverPrintService.GetSpineGuideOffsetsMm(CoverType.StandardCdFront));

        var cdBack = CoverPrintService.GetSpineGuideOffsetsMm(CoverType.StandardCdBack);
        Assert.Equal(2, cdBack.Count);
        Assert.Equal(6.5, cdBack[0], precision: 3);
        Assert.Equal(144.5, cdBack[1], precision: 3);

        var dvd = CoverPrintService.GetSpineGuideOffsetsMm(CoverType.DvdCase);
        Assert.Equal(2, dvd.Count);
        Assert.Equal(129.5, dvd[0], precision: 3);
        Assert.Equal(143.5, dvd[1], precision: 3);
    }

    [Fact]
    public void CreatePrintPlan_BuildsCompletePlanForDvdAndDefaultTitle()
    {
        var job = new CoverPrintJob
        {
            CoverType = CoverType.DvdCase,
            Title = "   ",
            Subtitle = "2026 Edition",
            Columns = 2,
            ShowNumbers = true
        };
        job.Items.AddRange(["Chapter 1", "Chapter 2"]);

        var plan = CoverPrintService.CreatePrintPlan(job);

        Assert.Equal("CDBurner10 Disc Cover", plan.JobTitle);
        Assert.Equal(CoverType.DvdCase, plan.Dimensions.Type);
        Assert.True(plan.PreferLandscape);
        Assert.Equal(2, plan.SpineGuideOffsetsMm.Count);
        Assert.Equal(2, plan.Columns.Count);
        Assert.Equal("2026 Edition", plan.Subtitle);
        Assert.Equal(0.28, plan.BackgroundOpacity, precision: 3);
    }

    [Fact]
    public void CreatePrintPlan_ClampsBackgroundOpacityToValidRange()
    {
        var lowPlan = CoverPrintService.CreatePrintPlan(new CoverPrintJob { BackgroundOpacity = -0.5 });
        var highPlan = CoverPrintService.CreatePrintPlan(new CoverPrintJob { BackgroundOpacity = 1.75 });
        var midPlan = CoverPrintService.CreatePrintPlan(new CoverPrintJob { BackgroundOpacity = 0.65 });

        Assert.Equal(0.0, lowPlan.BackgroundOpacity, precision: 3);
        Assert.Equal(1.0, highPlan.BackgroundOpacity, precision: 3);
        Assert.Equal(0.65, midPlan.BackgroundOpacity, precision: 3);
    }
}
