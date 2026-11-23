using System.ComponentModel;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CDBurner10.App.Controls;

public sealed record DiscPreset(string Name, long CapacityBytes, double CapacitySeconds, bool IsCustom = false)
{
    public override string ToString() => Name;
}

public sealed record AudioBarSegment(string Label, double DurationSeconds);

public sealed partial class DiscCapacityBar : UserControl
{
    private static readonly DiscPreset[] DataPresets =
    [
        new("CD (700 MB)", 734_003_200L, 4800),
        new("DVD (4.7 GB)", 4_700_000_000L, 0),
        new("DVD-DL (8.5 GB)", 8_500_000_000L, 0),
        new("Blu-ray (25 GB)", 25_025_314_816L, 0),
        new("Blu-ray DL (50 GB)", 50_050_629_632L, 0),
        new("Custom...", 734_003_200L, 0, IsCustom: true)
    ];

    private static readonly DiscPreset[] AudioPresets =
    [
        new("Audio CD (74 min)", 681_574_400L, 74 * 60),
        new("Audio CD (80 min)", 734_003_200L, 80 * 60),
        new("Audio CD (90 min)", 828_375_040L, 90 * 60),
        new("Audio CD (99 min)", 911_212_544L, 99 * 60)
    ];

    private const double CdDataBytesPerAudioSecond = 2048.0 * 75.0; // 75 sectors/sec * 2048 B/sector

    private static readonly Color[] NormalTrackPalette =
    [
        Color.FromArgb(255, 0, 120, 212),   // Fluent Blue
        Color.FromArgb(255, 16, 137, 118),  // Teal
        Color.FromArgb(255, 79, 107, 237),  // Indigo
        Color.FromArgb(255, 14, 165, 233),  // Sky
        Color.FromArgb(255, 16, 185, 129),  // Emerald
        Color.FromArgb(255, 139, 92, 246),  // Violet
    ];

    private static readonly Color[] AmberTrackPalette =
    [
        Color.FromArgb(255, 217, 119, 6),
        Color.FromArgb(255, 245, 158, 11),
    ];

    private static readonly Color[] RedTrackPalette =
    [
        Color.FromArgb(255, 220, 38, 38),
        Color.FromArgb(255, 239, 68, 68),
    ];

    private bool _isAudioMode;
    private long _usedBytes;
    private double _usedSeconds;
    private long _existingUsedBytes;
    private double _existingUsedSeconds;
    private long _capacityBytes = 734_003_200L;
    private double _capacitySeconds = 80 * 60;
    private bool _suppressSelectionChanged;
    private IReadOnlyList<AudioBarSegment> _audioTracks = [];

    public event EventHandler? CapacityChanged;

    public bool IsAudioMode
    {
        get => _isAudioMode;
        set
        {
            if (_isAudioMode == value) return;
            _isAudioMode = value;
            LoadPresets();
            ApplyInsertedDisc(AppServices.Current.SelectedDriveDiscInfo);
        }
    }

    public long UsedBytes
    {
        get => _usedBytes;
        set
        {
            _usedBytes = Math.Max(0, value);
            UpdateVisuals();
        }
    }

    public double UsedSeconds
    {
        get => _usedSeconds;
        set
        {
            _usedSeconds = Math.Max(0, value);
            UpdateVisuals();
        }
    }

    public void SetAudioTracks(IReadOnlyList<AudioBarSegment> tracks)
    {
        _audioTracks = tracks ?? [];
        _usedSeconds = Math.Max(0, _audioTracks.Sum(t => Math.Max(0, t.DurationSeconds)));
        UpdateVisuals();
    }

    public long ExistingUsedBytes
    {
        get => _existingUsedBytes;
        set
        {
            _existingUsedBytes = Math.Max(0, value);
            UpdateVisuals();
        }
    }

    public double ExistingUsedSeconds
    {
        get => _existingUsedSeconds;
        set
        {
            _existingUsedSeconds = Math.Max(0, value);
            UpdateVisuals();
        }
    }

    public long CapacityBytes => _capacityBytes;
    public double CapacitySeconds => _capacitySeconds;

    public bool IsOverburn => _isAudioMode
        ? _capacitySeconds > 0 && (_existingUsedSeconds + _usedSeconds) > _capacitySeconds
        : _capacityBytes > 0 && (_existingUsedBytes + _usedBytes) > _capacityBytes;

    public DiscCapacityBar()
    {
        InitializeComponent();
        LoadPresets();
        Loaded += DiscCapacityBar_Loaded;
        Unloaded += DiscCapacityBar_Unloaded;
        ApplyInsertedDisc(AppServices.Current.SelectedDriveDiscInfo);
        UpdateVisuals();
    }

    private void DiscCapacityBar_Loaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.PropertyChanged += OnAppServicesPropertyChanged;
        ApplyInsertedDisc(AppServices.Current.SelectedDriveDiscInfo);
    }

    private void DiscCapacityBar_Unloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.PropertyChanged -= OnAppServicesPropertyChanged;
    }

    private void OnAppServicesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppServices.SelectedDriveDiscInfo))
        {
            DispatcherQueue.TryEnqueue(() => ApplyInsertedDisc(AppServices.Current.SelectedDriveDiscInfo));
        }
    }

    public void ApplyInsertedDisc(DiscInfoModel? info)
    {
        bool capacityUpdated = false;

        if (info is not null && info.HasDisc && !info.IsBlank && info.UsedSizeBytes > 0)
        {
            _existingUsedBytes = info.UsedSizeBytes;
            _existingUsedSeconds = info.UsedSizeBytes / CdDataBytesPerAudioSecond;
        }
        else
        {
            _existingUsedBytes = 0;
            _existingUsedSeconds = 0;
        }

        if (info is not null && info.HasDisc && info.TotalSizeBytes > 0)
        {
            if (_isAudioMode && info.IsCd)
            {
                double detectedSeconds = info.TotalSizeBytes / CdDataBytesPerAudioSecond;
                double detectedMinutes = detectedSeconds / 60.0;
                int presetIdx = detectedMinutes switch
                {
                    < 77.0 => 0,  // 74 min
                    < 85.0 => 1,  // 80 min
                    < 94.5 => 2,  // 90 min
                    _ => 3        // 99 min
                };

                _capacityBytes = info.TotalSizeBytes;
                _capacitySeconds = detectedSeconds;
                SelectPresetSilently(presetIdx);
                capacityUpdated = true;
            }
            else if (!_isAudioMode)
            {
                int presetIdx;
                if (info.IsCd || info.TotalSizeBytes < 1_500_000_000L)
                    presetIdx = 0; // CD (700 MB)
                else if (info.DiscType is OpticalDiscType.DvdRDl or OpticalDiscType.DvdPlusRDl ||
                         (info.TotalSizeBytes >= 6_000_000_000L && info.TotalSizeBytes < 15_000_000_000L))
                    presetIdx = 2; // DVD-DL (8.5 GB)
                else if (info.TotalSizeBytes < 6_000_000_000L)
                    presetIdx = 1; // DVD (4.7 GB)
                else if (info.DiscType == OpticalDiscType.BdRDl || info.TotalSizeBytes >= 35_000_000_000L)
                    presetIdx = 4; // Blu-ray DL (50 GB)
                else
                    presetIdx = 3; // Blu-ray (25 GB)

                _capacityBytes = info.TotalSizeBytes;
                _capacitySeconds = info.IsCd ? info.TotalSizeBytes / CdDataBytesPerAudioSecond : 0;
                SelectPresetSilently(presetIdx);
                capacityUpdated = true;
            }
        }

        UpdateVisuals();
        if (capacityUpdated)
        {
            CapacityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SelectPresetSilently(int index)
    {
        if (index < 0 || index >= PresetComboBox.Items.Count) return;
        _suppressSelectionChanged = true;
        PresetComboBox.SelectedIndex = index;
        _suppressSelectionChanged = false;
    }

    public void SetCustomCapacity(long bytes)
    {
        if (bytes <= 0) return;
        _capacityBytes = bytes;
        _suppressSelectionChanged = true;
        PresetComboBox.SelectedIndex = PresetComboBox.Items.Count - 1;
        _suppressSelectionChanged = false;
        UpdateVisuals();
        CapacityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadPresets()
    {
        _suppressSelectionChanged = true;
        PresetComboBox.Items.Clear();

        if (_isAudioMode)
        {
            foreach (var p in AudioPresets)
                PresetComboBox.Items.Add(p);
            PresetComboBox.SelectedIndex = 1; // 80 min default
            _capacityBytes = AudioPresets[1].CapacityBytes;
            _capacitySeconds = AudioPresets[1].CapacitySeconds;
            CustomSizeButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            foreach (var p in DataPresets)
                PresetComboBox.Items.Add(p);
            PresetComboBox.SelectedIndex = 1; // DVD 4.7 GB default (or CD)
            _capacityBytes = DataPresets[1].CapacityBytes;
            _capacitySeconds = DataPresets[1].CapacitySeconds;
            CustomSizeButton.Visibility = Visibility.Visible;
        }

        _suppressSelectionChanged = false;
    }

    private async void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (PresetComboBox.SelectedItem is not DiscPreset preset) return;

        if (preset.IsCustom)
        {
            await PromptCustomSizeAsync();
            return;
        }

        _capacityBytes = preset.CapacityBytes;
        _capacitySeconds = preset.CapacitySeconds;
        UpdateVisuals();
        CapacityChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void CustomSizeButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptCustomSizeAsync();
    }

    private async Task PromptCustomSizeAsync()
    {
        if (XamlRoot is null) return;

        var mbBox = new NumberBox
        {
            Header = "Custom disc capacity (MB)",
            Value = Math.Round(_capacityBytes / (1024.0 * 1024.0)),
            Minimum = 1,
            Maximum = 1_000_000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };

        var dialog = new ContentDialog
        {
            Title = "Custom Disc Size",
            Content = mbBox,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !double.IsNaN(mbBox.Value))
        {
            long bytes = (long)(mbBox.Value * 1024.0 * 1024.0);
            SetCustomCapacity(bytes);
        }
    }

    private void BarTrack_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateVisuals();

    private void UpdateVisuals()
    {
        double existingRatio = _isAudioMode
            ? (_capacitySeconds > 0 ? _existingUsedSeconds / _capacitySeconds : 0)
            : (_capacityBytes > 0 ? (double)_existingUsedBytes / _capacityBytes : 0);

        double newRatio = _isAudioMode
            ? (_capacitySeconds > 0 ? _usedSeconds / _capacitySeconds : 0)
            : (_capacityBytes > 0 ? (double)_usedBytes / _capacityBytes : 0);

        double totalRatio = existingRatio + newRatio;
        double pct = totalRatio * 100.0;
        double trackWidth = BarTrack.ActualWidth;
        if (trackWidth > 0)
        {
            double existingWidth = Math.Clamp(existingRatio, 0, 1.0) * trackWidth;
            double remainingTrackWidth = Math.Max(0, trackWidth - existingWidth);
            double newWidth = Math.Min(Math.Clamp(newRatio, 0, 1.0) * trackWidth, remainingTrackWidth);

            ExistingBarFill.Width = existingWidth;
            ExistingBarFill.CornerRadius = newWidth > 0 ? new CornerRadius(5, 0, 0, 5) : new CornerRadius(5);

            bool renderPerTrack = _isAudioMode && _audioTracks.Count > 0 && _capacitySeconds > 0;
            if (renderPerTrack)
            {
                BarFill.Visibility = Visibility.Collapsed;
                TrackSegmentsCanvas.Visibility = Visibility.Visible;
                RenderAudioTrackSegments(trackWidth, existingWidth, totalRatio);
            }
            else
            {
                TrackSegmentsCanvas.Visibility = Visibility.Collapsed;
                TrackSegmentsCanvas.Children.Clear();
                BarFill.Visibility = Visibility.Visible;
                BarFill.Margin = new Thickness(existingWidth, 0, 0, 0);
                BarFill.Width = newWidth;
                BarFill.CornerRadius = existingWidth > 0 ? new CornerRadius(0, 5, 5, 0) : new CornerRadius(5);
            }

            WarningMarker.Margin = new Thickness(trackWidth * 0.9, 0, 0, 0);
        }

        if (totalRatio > 1.0)
        {
            BarFill.Background = new SolidColorBrush(Color.FromArgb(255, 220, 38, 38)); // Red
            OverburnBadge.Visibility = Visibility.Visible;
        }
        else if (totalRatio >= 0.90)
        {
            BarFill.Background = new SolidColorBrush(Color.FromArgb(255, 217, 119, 6)); // Amber
            OverburnBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            BarFill.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            OverburnBadge.Visibility = Visibility.Collapsed;
        }

        if (_isAudioMode)
        {
            double totalSec = _existingUsedSeconds + _usedSeconds;
            var used = TimeSpan.FromSeconds(_usedSeconds);
            var total = TimeSpan.FromSeconds(totalSec);
            var cap = TimeSpan.FromSeconds(_capacitySeconds);

            UsageSummaryText.Text = _existingUsedSeconds > 0
                ? $"{FormatHelper.FormatDuration(TimeSpan.FromSeconds(_existingUsedSeconds))} on disc + {FormatHelper.FormatDuration(used)} new ({FormatHelper.FormatDuration(total)} of {FormatHelper.FormatDuration(cap)})"
                : $"{FormatHelper.FormatDuration(used)} used of {FormatHelper.FormatDuration(cap)}";

            if (totalSec <= _capacitySeconds)
            {
                var rem = TimeSpan.FromSeconds(_capacitySeconds - totalSec);
                RemainingText.Text = $"{FormatHelper.FormatDuration(rem)} remaining ({pct:0.#}%)";
            }
            else
            {
                var over = TimeSpan.FromSeconds(totalSec - _capacitySeconds);
                RemainingText.Text = $"Exceeds capacity by {FormatHelper.FormatDuration(over)} ({pct:0.#}%)";
            }
        }
        else
        {
            long totalBytes = _existingUsedBytes + _usedBytes;
            UsageSummaryText.Text = _existingUsedBytes > 0
                ? $"{FormatHelper.FormatBytes(_existingUsedBytes)} on disc + {FormatHelper.FormatBytes(_usedBytes)} new ({FormatHelper.FormatBytes(totalBytes)} of {FormatHelper.FormatBytes(_capacityBytes)})"
                : $"{FormatHelper.FormatBytes(_usedBytes)} used of {FormatHelper.FormatBytes(_capacityBytes)}";

            if (totalBytes <= _capacityBytes)
            {
                RemainingText.Text = $"{FormatHelper.FormatBytes(_capacityBytes - totalBytes)} free ({pct:0.#}%)";
            }
            else
            {
                RemainingText.Text = $"Exceeds capacity by {FormatHelper.FormatBytes(totalBytes - _capacityBytes)} ({pct:0.#}%)";
            }
        }
    }

    private void RenderAudioTrackSegments(double trackWidth, double existingWidth, double totalRatio)
    {
        TrackSegmentsCanvas.Children.Clear();
        TrackSegmentsCanvas.Width = trackWidth;

        Color[] palette = totalRatio > 1.0
            ? RedTrackPalette
            : totalRatio >= 0.90
                ? AmberTrackPalette
                : NormalTrackPalette;

        double cursorX = existingWidth;
        const double gapPx = 1.0;

        for (int i = 0; i < _audioTracks.Count; i++)
        {
            if (cursorX >= trackWidth) break;

            var seg = _audioTracks[i];
            if (seg.DurationSeconds <= 0) continue;

            double rawW = (seg.DurationSeconds / _capacitySeconds) * trackWidth;
            double remaining = trackWidth - cursorX;
            double clampedW = Math.Min(rawW, remaining);
            bool hasNext = i < _audioTracks.Count - 1 && (cursorX + clampedW) < trackWidth;
            double drawW = hasNext && clampedW > (gapPx + 1.5) ? clampedW - gapPx : clampedW;
            if (drawW < 1.0) drawW = Math.Min(1.0, remaining);

            bool roundLeft = i == 0 && existingWidth <= 0.5;
            bool roundRight = !hasNext || (cursorX + drawW) >= trackWidth - 0.5;

            var border = new Border
            {
                Width = drawW,
                Height = 10,
                Background = new SolidColorBrush(palette[i % palette.Length]),
                CornerRadius = new CornerRadius(
                    roundLeft ? 5 : 0,
                    roundRight ? 5 : 0,
                    roundRight ? 5 : 0,
                    roundLeft ? 5 : 0)
            };

            string tip = $"{seg.Label} ({FormatHelper.FormatDuration(TimeSpan.FromSeconds(seg.DurationSeconds))})";
            ToolTipService.SetToolTip(border, tip);

            Canvas.SetLeft(border, cursorX);
            Canvas.SetTop(border, 0);
            TrackSegmentsCanvas.Children.Add(border);

            cursorX += clampedW;
        }
    }
}
