using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Printing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace CDBurner10.App.Views;

public sealed partial class CoverPrintPage : Page
{
    private readonly IReadOnlyList<CoverLayoutDimensions> _layouts;
    private string? _backgroundImagePath;
    private bool _isInitializing = true;

    public CoverPrintPage()
    {
        InitializeComponent();
        _layouts = CoverPrintService.GetAvailableLayouts();
        Loaded += CoverPrintPage_Loaded;
    }

    private void CoverPrintPage_Loaded(object sender, RoutedEventArgs e)
    {
        CoverTypeComboBox.ItemsSource = _layouts;
        CoverTypeComboBox.DisplayMemberPath = nameof(CoverLayoutDimensions.DisplayName);
        CoverTypeComboBox.SelectedIndex = 0;

        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            if (AppServices.Current.ActiveAudioCompilation.Tracks.Count > 0)
            {
                PopulateFromAudio();
            }
            else if (AppServices.Current.ActiveDataCompilation.DataBurner.FileCount > 0)
            {
                PopulateFromData();
            }
            else
            {
                TitleBox.Text = AppServices.Current.ActiveDataCompilation.Name;
                SubtitleBox.Text = DateTime.Now.ToString("yyyy-MM-dd");
            }
        }

        _isInitializing = false;
        UpdatePreview();
    }

    private CoverPrintJob BuildJob()
    {
        var layout = CoverTypeComboBox.SelectedItem as CoverLayoutDimensions ?? _layouts[0];
        var job = new CoverPrintJob
        {
            CoverType = layout.Type,
            Title = TitleBox.Text?.Trim() ?? string.Empty,
            Subtitle = SubtitleBox.Text?.Trim() ?? string.Empty,
            BackgroundImagePath = _backgroundImagePath,
            Columns = Math.Max(1, (int)ColumnsBox.Value),
            ShowNumbers = ShowNumbersCheckBox.IsChecked == true
        };

        var lines = (ItemsBox.Text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        job.Items.AddRange(lines);
        return job;
    }

    private void UpdatePreview()
    {
        if (_isInitializing) return;
        var layout = CoverTypeComboBox.SelectedItem as CoverLayoutDimensions ?? _layouts[0];
        var (pxW, pxH) = CoverPrintService.GetPixelSize(layout.Type, 300);
        DimensionsText.Text = $"{layout.WidthMm:0.#} × {layout.HeightMm:0.#} mm ({pxW} × {pxH} px @ 300 DPI)";

        // Scale preview proportionally to fit within 460x360 box
        const double maxW = 460;
        const double maxH = 360;
        double scale = Math.Min(maxW / layout.WidthMm, maxH / layout.HeightMm);
        CoverPreviewCard.Width = Math.Round(layout.WidthMm * scale);
        CoverPreviewCard.Height = Math.Round(layout.HeightMm * scale);

        var job = BuildJob();
        PreviewTitleText.Text = string.IsNullOrWhiteSpace(job.Title) ? "Untitled Disc" : job.Title;
        PreviewSubtitleText.Text = job.Subtitle;
        PreviewSubtitleText.Visibility = string.IsNullOrWhiteSpace(job.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        var formattedLines = job.Items.Select((item, idx) => job.ShowNumbers ? $"{idx + 1:D2}. {item}" : item);
        PreviewItemsText.Text = string.Join("\n", formattedLines);
    }

    private void CoverTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void DesignerInput_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void ColumnsBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdatePreview();
    private void DesignerOption_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private async void PickBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".png", ".jpg", ".jpeg", ".bmp"]);
        if (string.IsNullOrWhiteSpace(path)) return;

        _backgroundImagePath = path;
        BackgroundPathText.Text = Path.GetFileName(path);
        PreviewBackgroundImage.Source = new BitmapImage(new Uri(path));
    }

    private void ClearBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        _backgroundImagePath = null;
        BackgroundPathText.Text = "No background image selected";
        PreviewBackgroundImage.Source = null;
    }

    private void PopulateAudioButton_Click(object sender, RoutedEventArgs e) => PopulateFromAudio();
    private void PopulateDataButton_Click(object sender, RoutedEventArgs e) => PopulateFromData();

    private void PopulateFromAudio()
    {
        var comp = AppServices.Current.ActiveAudioCompilation;
        TitleBox.Text = string.IsNullOrWhiteSpace(comp.Title) ? "Audio CD" : comp.Title;
        SubtitleBox.Text = string.IsNullOrWhiteSpace(comp.Artist)
            ? $"{comp.Tracks.Count} tracks • {FormatHelper.FormatDuration(comp.TotalDuration)}"
            : $"{comp.Artist} • {FormatHelper.FormatDuration(comp.TotalDuration)}";

        ItemsBox.Text = string.Join(Environment.NewLine, comp.Tracks.Select(t =>
        {
            var label = string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{t.Artist} - {t.Title}";
            return $"{label} ({FormatHelper.FormatDuration(t.Duration)})";
        }));
        UpdatePreview();
    }

    private void PopulateFromData()
    {
        var comp = AppServices.Current.ActiveDataCompilation;
        TitleBox.Text = string.IsNullOrWhiteSpace(comp.Name) ? "Data Disc" : comp.Name;
        SubtitleBox.Text = $"{comp.DataBurner.FileCount} file(s) • {FormatHelper.FormatBytes(comp.DataBurner.TotalSizeBytes)}";

        var items = comp.RootFolder.Folders.Select(f => $"[{f.Name}]")
            .Concat(comp.RootFolder.Files.Select(f => $"{f.Name} ({FormatHelper.FormatBytes(f.FileSizeBytes)})"))
            .Take(80);
        ItemsBox.Text = string.Join(Environment.NewLine, items);
        UpdatePreview();
    }

    private void PrintCoverButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var job = BuildJob();
            var html = CoverPrintService.GeneratePrintableHtml(job);
            var tempHtml = AppServices.Current.Settings.GetTempFileName(".html");
            File.WriteAllText(tempHtml, html);

            Process.Start(new ProcessStartInfo(tempHtml) { UseShellExecute = true });
            ShowStatus(InfoBarSeverity.Success, "Opened Printable Cover",
                "Opened the formatted cover layout in your default browser — press Ctrl+P to print at exact millimeter dimensions.");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Print Failed", ex.Message);
        }
    }

    private async void ExportPngButton_Click(object sender, RoutedEventArgs e)
    {
        var suggested = string.IsNullOrWhiteSpace(TitleBox.Text) ? "DiscCover" : TitleBox.Text.Trim();
        var path = await FilePickerHelper.PickSaveFileAsync(suggested, new Dictionary<string, IList<string>>
        {
            ["PNG Image"] = [".png"]
        });
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(CoverPreviewCard, (int)CoverPreviewCard.Width * 2, (int)CoverPreviewCard.Height * 2);
            var pixels = await rtb.GetPixelsAsync();
            byte[] bytes = pixels.ToArray();

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth,
                (uint)rtb.PixelHeight,
                96, 96,
                bytes);
            await encoder.FlushAsync();

            stream.Seek(0);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            byte[] pngBytes = new byte[stream.Size];
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(pngBytes);
            await File.WriteAllBytesAsync(path, pngBytes);

            ShowStatus(InfoBarSeverity.Success, "Exported PNG", $"Saved cover image to '{Path.GetFileName(path)}'.");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Export Failed", ex.Message);
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
