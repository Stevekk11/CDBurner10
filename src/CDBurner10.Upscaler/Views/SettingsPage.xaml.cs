using System.Diagnostics;
using CDBurner10.Core.Utilities;
using CDBurner10.Upscaler.Engine;
using CDBurner10.Upscaler.Services;
using CDBurner10.Upscaler.Views.Dialogs;
using CDBurner10.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.Upscaler.Views;

public sealed partial class SettingsPage : Page
{
    public const string GitHubUrl = "https://github.com/Stevekk11/CDBurner10";

    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettingsToUi();
    }

    private void LoadSettingsToUi()
    {
        _loading = true;
        var svc = UpscalerAppServices.Current.Settings;
        var s = svc.Current;

        SettingsPathSubtitle.Text = svc.IsPortableMode
            ? $"Portable Mode • Settings stored at: {svc.SettingsFilePath}"
            : $"Settings stored at: {svc.SettingsFilePath}";

        // Theme
        SelectComboByTag(ThemeComboBox, s.Theme);
        ApplyThemeToWindow(s.Theme);

        // Notifications & Tray
        ShowNotificationsToggle.IsOn = s.ShowNotifications;
        ShowProgressNotificationsToggle.IsOn = s.ShowProgressNotifications;
        ExitToTrayToggle.IsOn = s.ExitToNotify;

        // libmpv, Shaders & ONNX Engine
        LibMpvStatusText.Text = UpscalerAppServices.Current.LibMpvStatusText;
        CustomLibMpvPathBox.Text = s.CustomLibMpvPath;
        SelectComboByTag(Anime4KTierComboBox, s.Anime4KTier);
        SelectComboByTag(MpvRendererComboBox, s.MpvRenderer);
        SelectComboByTag(MpvHwdecComboBox, s.MpvHwdec);
        SelectComboByTag(MpvScalerComboBox, s.MpvScaler);
        SelectComboByTag(OnnxProviderComboBox, s.OnnxProvider);
        OnnxTileSizeBox.Value = Math.Clamp(s.OnnxTileSize, 128, 1024);

        // Encoding & Output
        SelectComboByTag(VideoCodecComboBox, s.VideoCodec);
        SelectComboByTag(EncoderPreferenceComboBox, s.EncoderPreference);
        SelectComboByTag(EncoderPresetComboBox, s.EncoderPreset);
        CrfBox.Value = Math.Clamp(s.Crf, 10, 32);
        SelectComboByTag(AudioModeComboBox, s.AudioMode);
        DefaultOutputFolderBox.Text = s.DefaultOutputFolder;
        OverwriteExistingToggle.IsOn = s.OverwriteExisting;

        // Diagnostics
        LoggingEnabledToggle.IsOn = s.LoggingEnabled;
        SelectComboByTag(LoggingLevelComboBox, s.LoggingLevel.ToString());

        _loading = false;
    }

    private void SaveUiToSettings()
    {
        if (_loading) return;
        var s = UpscalerAppServices.Current.Settings.Current;

        s.Theme = GetSelectedTag(ThemeComboBox);
        if (string.IsNullOrWhiteSpace(s.Theme)) s.Theme = "Default";

        s.ShowNotifications = ShowNotificationsToggle.IsOn;
        s.ShowProgressNotifications = ShowProgressNotificationsToggle.IsOn;
        s.ExitToNotify = ExitToTrayToggle.IsOn;

        string prevMpvPath = s.CustomLibMpvPath;
        s.CustomLibMpvPath = CustomLibMpvPathBox.Text?.Trim() ?? string.Empty;
        s.Anime4KTier = OrDefault(GetSelectedTag(Anime4KTierComboBox), "HQ");
        s.MpvRenderer = OrDefault(GetSelectedTag(MpvRendererComboBox), "gpu-next");
        s.MpvHwdec = OrDefault(GetSelectedTag(MpvHwdecComboBox), "auto-copy");
        s.MpvScaler = OrDefault(GetSelectedTag(MpvScalerComboBox), "ewa_lanczossharp");
        s.OnnxProvider = OrDefault(GetSelectedTag(OnnxProviderComboBox), "DirectML");
        if (!double.IsNaN(OnnxTileSizeBox.Value))
            s.OnnxTileSize = Math.Clamp((int)OnnxTileSizeBox.Value, 128, 1024);

        s.VideoCodec = OrDefault(GetSelectedTag(VideoCodecComboBox), "h264");
        s.EncoderPreference = OrDefault(GetSelectedTag(EncoderPreferenceComboBox), "Auto");
        s.EncoderPreset = OrDefault(GetSelectedTag(EncoderPresetComboBox), "slow");
        if (!double.IsNaN(CrfBox.Value))
            s.Crf = Math.Clamp((int)CrfBox.Value, 10, 32);
        s.AudioMode = OrDefault(GetSelectedTag(AudioModeComboBox), "Auto");
        s.DefaultOutputFolder = DefaultOutputFolderBox.Text?.Trim() ?? string.Empty;
        s.OverwriteExisting = OverwriteExistingToggle.IsOn;

        s.LoggingEnabled = LoggingEnabledToggle.IsOn;
        if (int.TryParse(GetSelectedTag(LoggingLevelComboBox), out int logLvl))
            s.LoggingLevel = logLvl;

        UpscalerAppServices.Current.Settings.Save();

        if (!string.Equals(prevMpvPath, s.CustomLibMpvPath, StringComparison.OrdinalIgnoreCase))
        {
            UpscalerAppServices.Current.RefreshEngineStatus();
            LibMpvStatusText.Text = UpscalerAppServices.Current.LibMpvStatusText;
        }
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string tag = OrDefault(GetSelectedTag(ThemeComboBox), "Default");
        ApplyThemeToWindow(tag);
        SaveUiToSettings();
    }

    private static void ApplyThemeToWindow(string tag)
    {
        var theme = tag switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (App.MainWindow?.Content is FrameworkElement root)
            root.RequestedTheme = theme;
    }

    private void SettingControl_Changed(object sender, RoutedEventArgs e) => SaveUiToSettings();
    private void SettingText_Changed(object sender, TextChangedEventArgs e) => SaveUiToSettings();
    private void SettingNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => SaveUiToSettings();

    private void DownloadLibMpvButton_Click(object sender, RoutedEventArgs e) =>
        OpenExternalUrl(LibMpvNative.FallbackDownloadUrl);

    private async void RefreshStatusButton_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => UpscalerAppServices.Current.RefreshEngineStatus());
        LibMpvStatusText.Text = UpscalerAppServices.Current.LibMpvStatusText;
    }

    private async void BrowseLibMpvButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".dll"]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            CustomLibMpvPathBox.Text = path;
            SaveUiToSettings();
        }
    }

    private void ClearLibMpvButton_Click(object sender, RoutedEventArgs e)
    {
        CustomLibMpvPathBox.Text = string.Empty;
        SaveUiToSettings();
    }

    private async void BrowseDefaultOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await FilePickerHelper.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            DefaultOutputFolderBox.Text = folder;
            SaveUiToSettings();
        }
    }

    private void ClearDefaultOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        DefaultOutputFolderBox.Text = string.Empty;
        SaveUiToSettings();
    }

    private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = DiagnosticLogger.LogDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Cannot Open Logs Folder";
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
        }
    }

    private async void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTheme = (App.MainWindow?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
        var confirmDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = activeTheme,
            Title = "Reset Settings",
            Content = "Reset all CDBurner10 Upscaler preferences back to factory defaults?",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            CornerRadius = new CornerRadius(8)
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        UpscalerAppServices.Current.Settings.ResetToDefaults();
        UpscalerAppServices.Current.RefreshEngineStatus();
        LoadSettingsToUi();

        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = "Defaults Restored";
        StatusInfoBar.Message = "All upscaler settings have been reset to their default values.";
        StatusInfoBar.IsOpen = true;
    }

    private void GitHubButton_Click(object sender, RoutedEventArgs e) =>
        OpenExternalUrl(GitHubUrl);

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTheme = (App.MainWindow?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
        var dialog = new AboutDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = activeTheme,
            CornerRadius = new CornerRadius(8)
        };
        await dialog.ShowAsync();
    }

    internal static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static string OrDefault(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string GetSelectedTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
}