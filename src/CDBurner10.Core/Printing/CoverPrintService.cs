using System.Text;

namespace CDBurner10.Core.Printing;

public enum CoverType
{
    StandardCdFront,
    StandardCdInside,
    StandardCdBack,
    SlimCd,
    MiniCd,
    DvdCase,
    ContactSheet
}

public sealed record CoverLayoutDimensions(
    CoverType Type,
    string DisplayName,
    double WidthMm,
    double HeightMm,
    double SpineWidthMm = 0);

public sealed record CoverPrintPlan(
    string JobTitle,
    CoverLayoutDimensions Dimensions,
    double WidthDips,
    double HeightDips,
    IReadOnlyList<double> SpineGuideOffsetsMm,
    string DisplayTitle,
    string Subtitle,
    IReadOnlyList<IReadOnlyList<string>> Columns,
    string? BackgroundImagePath,
    double BackgroundOpacity,
    bool PreferLandscape);

public sealed class CoverPrintJob
{
    public CoverType CoverType { get; set; } = CoverType.StandardCdFront;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string? BackgroundImagePath { get; set; }
    public double BackgroundOpacity { get; set; } = 0.28;
    public List<string> Items { get; } = [];
    public int Columns { get; set; } = 1;
    public bool ShowNumbers { get; set; } = true;
}

public static class CoverPrintService
{
    public const double DipsPerInch = 96.0;
    public const double MmPerInch = 25.4;

    private static readonly IReadOnlyDictionary<CoverType, CoverLayoutDimensions> Layouts =
        new Dictionary<CoverType, CoverLayoutDimensions>
        {
            [CoverType.StandardCdFront] = new(CoverType.StandardCdFront, "Standard CD - Front", 120.0, 120.0),
            [CoverType.StandardCdInside] = new(CoverType.StandardCdInside, "Standard CD - Inside", 120.0, 120.0),
            [CoverType.StandardCdBack] = new(CoverType.StandardCdBack, "Standard CD - Back (Inlay)", 151.0, 118.0, 6.5),
            [CoverType.SlimCd] = new(CoverType.SlimCd, "Slim CD Case", 120.0, 120.0),
            [CoverType.MiniCd] = new(CoverType.MiniCd, "Mini CD (8 cm)", 85.0, 85.0),
            [CoverType.DvdCase] = new(CoverType.DvdCase, "DVD Case (Full Wrap)", 273.0, 183.0, 14.0),
            [CoverType.ContactSheet] = new(CoverType.ContactSheet, "Image Contact Sheet (A4)", 210.0, 297.0)
        };

    public static IReadOnlyList<CoverLayoutDimensions> GetAvailableLayouts() => Layouts.Values.ToList();

    public static CoverLayoutDimensions GetDimensions(CoverType type) => Layouts[type];

    public static double MmToDips(double millimeters) => millimeters / MmPerInch * DipsPerInch;

    public static (int WidthPx, int HeightPx) GetPixelSize(CoverType type, int dpi = 300)
    {
        var dims = Layouts[type];
        int w = (int)Math.Round(dims.WidthMm / MmPerInch * dpi);
        int h = (int)Math.Round(dims.HeightMm / MmPerInch * dpi);
        return (w, h);
    }

    public static string GetJobTitle(CoverPrintJob job)
    {
        var title = job.Title.Trim();
        return string.IsNullOrWhiteSpace(title)
            ? "CDBurner10 Disc Cover"
            : $"CDBurner10 - {title}";
    }

    public static IReadOnlyList<string> FormatItems(CoverPrintJob job)
    {
        var formatted = new List<string>(job.Items.Count);
        for (int i = 0; i < job.Items.Count; i++)
        {
            formatted.Add(job.ShowNumbers ? $"{i + 1:D2}. {job.Items[i]}" : job.Items[i]);
        }
        return formatted;
    }

    public static IReadOnlyList<IReadOnlyList<string>> SplitItemsIntoColumns(CoverPrintJob job)
    {
        var formatted = FormatItems(job);
        if (formatted.Count == 0)
            return [];

        int colCount = Math.Min(Math.Max(1, job.Columns), formatted.Count);
        int baseSize = formatted.Count / colCount;
        int remainder = formatted.Count % colCount;
        int offset = 0;

        var columns = new List<IReadOnlyList<string>>(colCount);
        for (int c = 0; c < colCount; c++)
        {
            int take = baseSize + (c < remainder ? 1 : 0);
            columns.Add(formatted.Skip(offset).Take(take).ToList());
            offset += take;
        }
        return columns;
    }

    public static IReadOnlyList<double> GetSpineGuideOffsetsMm(CoverType type)
    {
        var dims = Layouts[type];
        if (dims.SpineWidthMm <= 0)
            return [];

        return type switch
        {
            CoverType.StandardCdBack => [dims.SpineWidthMm, dims.WidthMm - dims.SpineWidthMm],
            CoverType.DvdCase => [(dims.WidthMm - dims.SpineWidthMm) / 2.0, (dims.WidthMm + dims.SpineWidthMm) / 2.0],
            _ => []
        };
    }

    public static CoverPrintPlan CreatePrintPlan(CoverPrintJob job)
    {
        var dims = GetDimensions(job.CoverType);
        return new CoverPrintPlan(
            JobTitle: GetJobTitle(job),
            Dimensions: dims,
            WidthDips: MmToDips(dims.WidthMm),
            HeightDips: MmToDips(dims.HeightMm),
            SpineGuideOffsetsMm: GetSpineGuideOffsetsMm(job.CoverType),
            DisplayTitle: job.Title.Trim(),
            Subtitle: job.Subtitle.Trim(),
            Columns: SplitItemsIntoColumns(job),
            BackgroundImagePath: job.BackgroundImagePath,
            BackgroundOpacity: Math.Clamp(job.BackgroundOpacity, 0.0, 1.0),
            PreferLandscape: dims.WidthMm > 190.0 && dims.WidthMm > dims.HeightMm);
    }

    public static string FormatTrackListText(CoverPrintJob job)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(job.Title))
        {
            sb.AppendLine(job.Title);
        }
        if (!string.IsNullOrWhiteSpace(job.Subtitle))
        {
            sb.AppendLine(job.Subtitle);
        }
        if (sb.Length > 0)
        {
            sb.AppendLine(new string('-', 32));
        }

        foreach (var line in FormatItems(job))
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }
}
