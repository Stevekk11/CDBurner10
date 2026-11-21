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

public sealed class CoverPrintJob
{
    public CoverType CoverType { get; set; } = CoverType.StandardCdFront;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string? BackgroundImagePath { get; set; }
    public List<string> Items { get; } = [];
    public int Columns { get; set; } = 1;
    public bool ShowNumbers { get; set; } = true;
}

public static class CoverPrintService
{
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

    public static (int WidthPx, int HeightPx) GetPixelSize(CoverType type, int dpi = 300)
    {
        var dims = Layouts[type];
        int w = (int)Math.Round(dims.WidthMm / 25.4 * dpi);
        int h = (int)Math.Round(dims.HeightMm / 25.4 * dpi);
        return (w, h);
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

        for (int i = 0; i < job.Items.Count; i++)
        {
            if (job.ShowNumbers)
                sb.AppendLine($"{i + 1:D2}. {job.Items[i]}");
            else
                sb.AppendLine(job.Items[i]);
        }

        return sb.ToString();
    }

    public static string GeneratePrintableHtml(CoverPrintJob job)
    {
        var dims = GetDimensions(job.CoverType);
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>");
        sb.AppendLine($"@page {{ size: auto; margin: 10mm; }}");
        sb.AppendLine($"body {{ font-family: 'Segoe UI', sans-serif; margin: 0; padding: 12px; }}");
        sb.AppendLine($".cover {{ width: {dims.WidthMm:F1}mm; height: {dims.HeightMm:F1}mm; border: 1px dashed #666; box-sizing: border-box; padding: 6mm; overflow: hidden; position: relative; }}");
        sb.AppendLine(".title { font-size: 16pt; font-weight: 600; margin-bottom: 2mm; }");
        sb.AppendLine(".subtitle { font-size: 10pt; color: #555; margin-bottom: 4mm; }");
        sb.AppendLine($".items {{ font-size: 9pt; column-count: {Math.Max(1, job.Columns)}; line-height: 1.4; }}");
        sb.AppendLine("</style></head><body><div class=\"cover\">");
        if (!string.IsNullOrWhiteSpace(job.Title))
            sb.AppendLine($"<div class=\"title\">{System.Net.WebUtility.HtmlEncode(job.Title)}</div>");
        if (!string.IsNullOrWhiteSpace(job.Subtitle))
            sb.AppendLine($"<div class=\"subtitle\">{System.Net.WebUtility.HtmlEncode(job.Subtitle)}</div>");
        sb.AppendLine("<div class=\"items\">");
        for (int i = 0; i < job.Items.Count; i++)
        {
            var text = job.ShowNumbers ? $"{i + 1:D2}. {job.Items[i]}" : job.Items[i];
            sb.AppendLine($"<div>{System.Net.WebUtility.HtmlEncode(text)}</div>");
        }
        sb.AppendLine("</div></div></body></html>");
        return sb.ToString();
    }
}
