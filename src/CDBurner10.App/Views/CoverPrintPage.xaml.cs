using System.Runtime.InteropServices.WindowsRuntime;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Printing;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Graphics.Printing;
using Windows.Storage.Streams;
using Windows.UI;

namespace CDBurner10.App.Views;

public sealed partial class CoverPrintPage : Page
{
    private readonly IReadOnlyList<CoverLayoutDimensions> _layouts;
    private readonly List<UIElement> _previewPages = [];
    private string? _backgroundImagePath;
    private bool _isInitializing = true;

    private nint _hWnd;
    private PrintManager? _printManager;
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _printDocumentSource;
    private CoverPrintPlan? _activePrintPlan;
    private BitmapImage? _preloadedBackgroundImage;

    public CoverPrintPage()
    {
        InitializeComponent();
        _layouts = CoverPrintService.GetAvailableLayouts();
        Loaded += CoverPrintPage_Loaded;
        Unloaded += CoverPrintPage_Unloaded;
    }

    private void CoverPrintPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (CoverTypeComboBox.ItemsSource is null)
        {
            CoverTypeComboBox.ItemsSource = _layouts;
            CoverTypeComboBox.DisplayMemberPath = nameof(CoverLayoutDimensions.DisplayName);
            CoverTypeComboBox.SelectedIndex = 0;
        }

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
        RegisterForPrinting();
    }

    private void CoverPrintPage_Unloaded(object sender, RoutedEventArgs e)
    {
        UnregisterForPrinting();
    }

    private void RegisterForPrinting()
    {
        UnregisterForPrinting();

        _hWnd = FilePickerHelper.GetMainWindowHandle();
        if (_hWnd == nint.Zero || !PrintManager.IsSupported())
            return;

        _printManager = PrintManagerInterop.GetForWindow(_hWnd);
        _printManager.PrintTaskRequested += PrintManager_PrintTaskRequested;

        _printDocument = new PrintDocument();
        _printDocumentSource = _printDocument.DocumentSource;
        _printDocument.Paginate += PrintDocument_Paginate;
        _printDocument.GetPreviewPage += PrintDocument_GetPreviewPage;
        _printDocument.AddPages += PrintDocument_AddPages;
    }

    private void UnregisterForPrinting()
    {
        if (_printDocument is not null)
        {
            _printDocument.Paginate -= PrintDocument_Paginate;
            _printDocument.GetPreviewPage -= PrintDocument_GetPreviewPage;
            _printDocument.AddPages -= PrintDocument_AddPages;
            _printDocument = null;
            _printDocumentSource = null;
        }

        if (_printManager is not null)
        {
            _printManager.PrintTaskRequested -= PrintManager_PrintTaskRequested;
            _printManager = null;
        }

        _previewPages.Clear();
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
            BackgroundOpacity = Math.Clamp(BackgroundOpacitySlider.Value / 100.0, 0.0, 1.0),
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
        PreviewBackgroundImage.Opacity = job.BackgroundOpacity;
        BackgroundOpacityText.Text = $"{Math.Round(job.BackgroundOpacity * 100):0}%";
        PreviewTitleText.Text = string.IsNullOrWhiteSpace(job.Title) ? "Untitled Disc" : job.Title;
        PreviewSubtitleText.Text = job.Subtitle;
        PreviewSubtitleText.Visibility = string.IsNullOrWhiteSpace(job.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        PreviewItemsText.Text = string.Join("\n", CoverPrintService.FormatItems(job));
    }

    private void CoverTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void DesignerInput_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void ColumnsBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdatePreview();
    private void DesignerOption_Changed(object sender, RoutedEventArgs e) => UpdatePreview();
    private void BackgroundOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => UpdatePreview();

    private async void PickBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".png", ".jpg", ".jpeg", ".bmp"]);
        if (string.IsNullOrWhiteSpace(path)) return;

        _backgroundImagePath = path;
        BackgroundPathText.Text = System.IO.Path.GetFileName(path);
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

    private async void PrintCoverButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!PrintManager.IsSupported())
            {
                ShowStatus(InfoBarSeverity.Warning, "Printing Unavailable",
                    "Windows printing is not supported in this environment.");
                return;
            }

            if (_printManager is null || _printDocument is null)
                RegisterForPrinting();

            if (_hWnd == nint.Zero || _printManager is null || _printDocument is null)
            {
                ShowStatus(InfoBarSeverity.Error, "Print Failed",
                    "Could not initialize the Windows print manager for this window.");
                return;
            }

            _activePrintPlan = CoverPrintService.CreatePrintPlan(BuildJob());
            _preloadedBackgroundImage = null;

            if (!string.IsNullOrWhiteSpace(_activePrintPlan.BackgroundImagePath) &&
                File.Exists(_activePrintPlan.BackgroundImagePath))
            {
                _preloadedBackgroundImage = await LoadBitmapForPrintAsync(_activePrintPlan.BackgroundImagePath);
            }

            bool shown = await PrintManagerInterop.ShowPrintUIForWindowAsync(_hWnd);
            if (!shown)
            {
                ShowStatus(InfoBarSeverity.Warning, "Print Dialog Unavailable",
                    "The Windows print dialog could not be opened.");
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Print Failed", ex.Message);
        }
    }

    private static async Task<BitmapImage> LoadBitmapForPrintAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var randomAccessStream = stream.AsRandomAccessStream();
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(randomAccessStream);
        return bitmap;
    }

    private void PrintManager_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        var plan = _activePrintPlan;
        var source = _printDocumentSource;
        if (plan is null || source is null) return;

        var printTask = args.Request.CreatePrintTask(plan.JobTitle, sourceRequestedArgs =>
        {
            sourceRequestedArgs.SetSource(source);
        });

        if (plan.PreferLandscape)
        {
            printTask.Options.Orientation = PrintOrientation.Landscape;
        }

        printTask.Completed += (_, completedArgs) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                switch (completedArgs.Completion)
                {
                    case PrintTaskCompletion.Submitted:
                        ShowStatus(InfoBarSeverity.Success, "Print Job Submitted",
                            $"Sent '{plan.JobTitle}' to the printer.");
                        break;
                    case PrintTaskCompletion.Canceled:
                    case PrintTaskCompletion.Abandoned:
                        ShowStatus(InfoBarSeverity.Informational, "Print Canceled",
                            "The print job was canceled.");
                        break;
                    case PrintTaskCompletion.Failed:
                        ShowStatus(InfoBarSeverity.Error, "Print Failed",
                            "The print job could not be completed.");
                        break;
                }
            });
        };
    }

    private void PrintDocument_Paginate(object sender, PaginateEventArgs e)
    {
        _previewPages.Clear();

        if (_activePrintPlan is not null)
        {
            var pageDesc = e.PrintTaskOptions.GetPageDescription(0);
            _previewPages.Add(BuildPrintablePage(_activePrintPlan, pageDesc.PageSize, pageDesc.ImageableRect));
        }

        _printDocument?.SetPreviewPageCount(_previewPages.Count, PreviewPageCountType.Final);
    }

    private void PrintDocument_GetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        if (e.PageNumber >= 1 && e.PageNumber <= _previewPages.Count)
        {
            _printDocument?.SetPreviewPage(e.PageNumber, _previewPages[e.PageNumber - 1]);
        }
    }

    private void PrintDocument_AddPages(object sender, AddPagesEventArgs e)
    {
        if (_activePrintPlan is not null && _printDocument is not null)
        {
            var pageDesc = e.PrintTaskOptions.GetPageDescription(0);
            _printDocument.AddPage(BuildPrintablePage(_activePrintPlan, pageDesc.PageSize, pageDesc.ImageableRect));
        }

        _printDocument?.AddPagesComplete();
    }

    private UIElement BuildPrintablePage(CoverPrintPlan plan, Size pageSize, Rect imageableRect)
    {
        double coverWidth = plan.WidthDips;
        double coverHeight = plan.HeightDips;

        var coverRoot = new Grid
        {
            Width = coverWidth,
            Height = coverHeight,
            Background = new SolidColorBrush(Colors.White)
        };

        if (_preloadedBackgroundImage is not null)
        {
            coverRoot.Children.Add(new Image
            {
                Source = _preloadedBackgroundImage,
                Stretch = Stretch.UniformToFill,
                Opacity = plan.BackgroundOpacity
            });
        }

        foreach (double offsetMm in plan.SpineGuideOffsetsMm)
        {
            double x = CoverPrintService.MmToDips(offsetMm);
            coverRoot.Children.Add(new Line
            {
                X1 = x,
                Y1 = 0,
                X2 = x,
                Y2 = coverHeight,
                Stroke = new SolidColorBrush(Color.FromArgb(160, 130, 130, 130)),
                StrokeThickness = 0.75,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            });
        }

        if (plan.Dimensions.Type == CoverType.DvdCase)
        {
            coverRoot.Children.Add(BuildDvdWrapContent(plan));
        }
        else
        {
            coverRoot.Children.Add(BuildStandardCoverContent(plan));
        }

        coverRoot.Children.Add(new Rectangle
        {
            Width = coverWidth,
            Height = coverHeight,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 102, 102, 102)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 }
        });

        double availW = imageableRect.Width > 0 ? imageableRect.Width : pageSize.Width;
        double availH = imageableRect.Height > 0 ? imageableRect.Height : pageSize.Height;
        double scale = (availW > 0 && availH > 0)
            ? Math.Min(1.0, Math.Min(availW / coverWidth, availH / coverHeight))
            : 1.0;

        FrameworkElement placedCover = scale < 0.999
            ? new Viewbox
            {
                Width = coverWidth * scale,
                Height = coverHeight * scale,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = coverRoot
            }
            : coverRoot;

        placedCover.HorizontalAlignment = HorizontalAlignment.Center;
        placedCover.VerticalAlignment = VerticalAlignment.Center;

        var pageCanvas = new Grid
        {
            Width = pageSize.Width > 0 ? pageSize.Width : coverWidth,
            Height = pageSize.Height > 0 ? pageSize.Height : coverHeight,
            Background = new SolidColorBrush(Colors.White)
        };
        pageCanvas.Children.Add(placedCover);
        return pageCanvas;
    }

    private static Grid BuildStandardCoverContent(CoverPrintPlan plan)
    {
        double sidePad = plan.Dimensions.Type == CoverType.StandardCdBack
            ? CoverPrintService.MmToDips(plan.Dimensions.SpineWidthMm + 5.0)
            : CoverPrintService.MmToDips(6.0);
        double topBottomPad = CoverPrintService.MmToDips(6.0);

        var contentGrid = new Grid
        {
            Padding = new Thickness(sidePad, topBottomPad, sidePad, topBottomPad)
        };
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        if (!string.IsNullOrWhiteSpace(plan.DisplayTitle))
        {
            var titleBlock = CreateTitleBlock(plan.DisplayTitle);
            Grid.SetRow(titleBlock, 0);
            contentGrid.Children.Add(titleBlock);
        }

        if (!string.IsNullOrWhiteSpace(plan.Subtitle))
        {
            var subtitleBlock = CreateSubtitleBlock(plan.Subtitle);
            Grid.SetRow(subtitleBlock, 1);
            contentGrid.Children.Add(subtitleBlock);
        }

        if (plan.Columns.Count > 0)
        {
            var columnsGrid = CreateColumnsGrid(plan.Columns);
            Grid.SetRow(columnsGrid, 2);
            contentGrid.Children.Add(columnsGrid);
        }

        return contentGrid;
    }

    private static Grid BuildDvdWrapContent(CoverPrintPlan plan)
    {
        double panelWidthMm = (plan.Dimensions.WidthMm - plan.Dimensions.SpineWidthMm) / 2.0;
        double pad = CoverPrintService.MmToDips(8.0);

        var wrapGrid = new Grid();
        wrapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CoverPrintService.MmToDips(panelWidthMm)) });
        wrapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CoverPrintService.MmToDips(plan.Dimensions.SpineWidthMm)) });
        wrapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CoverPrintService.MmToDips(panelWidthMm)) });

        // Back panel (left): track / file listing
        if (plan.Columns.Count > 0)
        {
            var backPanel = new Grid { Padding = new Thickness(pad) };
            backPanel.Children.Add(CreateColumnsGrid(plan.Columns));
            Grid.SetColumn(backPanel, 0);
            wrapGrid.Children.Add(backPanel);
        }

        // Front panel (right): title & subtitle
        var frontPanel = new StackPanel
        {
            Padding = new Thickness(pad),
            VerticalAlignment = VerticalAlignment.Top
        };
        if (!string.IsNullOrWhiteSpace(plan.DisplayTitle))
            frontPanel.Children.Add(CreateTitleBlock(plan.DisplayTitle));
        if (!string.IsNullOrWhiteSpace(plan.Subtitle))
            frontPanel.Children.Add(CreateSubtitleBlock(plan.Subtitle));

        Grid.SetColumn(frontPanel, 2);
        wrapGrid.Children.Add(frontPanel);

        return wrapGrid;
    }

    private static TextBlock CreateTitleBlock(string title) => new()
    {
        Text = title,
        FontSize = 21.33,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 17, 17, 17)),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private static TextBlock CreateSubtitleBlock(string subtitle) => new()
    {
        Text = subtitle,
        FontSize = 13.33,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 85, 85, 85)),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10)
    };

    private static Grid CreateColumnsGrid(IReadOnlyList<IReadOnlyList<string>> columns)
    {
        var columnsGrid = new Grid { ColumnSpacing = 14 };
        for (int c = 0; c < columns.Count; c++)
        {
            columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var colText = new TextBlock
            {
                Text = string.Join("\n", columns[c]),
                FontSize = 12.0,
                LineHeight = 16.8,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 34, 34, 34)),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(colText, c);
            columnsGrid.Children.Add(colText);
        }
        return columnsGrid;
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

            ShowStatus(InfoBarSeverity.Success, "Exported PNG", $"Saved cover image to '{System.IO.Path.GetFileName(path)}'.");
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
