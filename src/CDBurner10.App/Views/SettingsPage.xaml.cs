using System.Diagnostics;
using CDBurner10.App.Services;
using CDBurner10.App.Views.Dialogs;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Settings;
using CDBurner10.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views;

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
        var svc = AppServices.Current.Settings;
        var s = svc.Current;

        SettingsPathSubtitle.Text = svc.IsPortableMode
            ? $"Portable Mode • Settings stored at: {svc.SettingsFilePath}"
            : $"Settings stored at: {svc.SettingsFilePath}";

        // Current window theme
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            ThemeComboBox.SelectedIndex = root.RequestedTheme switch
            {
                ElementTheme.Light => 1,
                ElementTheme.Dark => 2,
                _ => 0
            };
        }

        // Notifications
        ShowNotificationsToggle.IsOn = s.ShowNotifications;
        ShowProgressNotificationsToggle.IsOn = s.ShowProgressNotifications;

        // General
        SelectComboByTag(LanguageComboBox, s.AppLanguage);
        RememberLastProjectToggle.IsOn = s.StartLastUsedProjectType;
        AskHowToBurnDataToggle.IsOn = s.DataAskForSettings;
        ExitToTrayToggle.IsOn = s.ExitToNotify;
        UseCustomTempToggle.IsOn = s.UseCustomTempFolder;
        CustomTempFolderBox.Text = s.CustomTempFolder;
        CustomTempFolderBox.IsEnabled = s.UseCustomTempFolder;
        LoggingEnabledToggle.IsOn = s.LoggingEnabled;
        SelectComboByTag(LoggingLevelComboBox, s.LoggingLevel.ToString());

        // Data
        DefaultDiscLabelBox.Text = s.DataLabel;
        UpdateDiscLabelPreview();
        AutoCacheRemovableToggle.IsOn = s.DataCacheFilesAutomatically;
        AutoCacheNetworkToggle.IsOn = s.DataCacheNetworkFilesAutomatically;
        AutoUpdateCompilationsToggle.IsOn = s.DataUpdateAutomatically;
        IgnoreFileRenameToggle.IsOn = s.IgnoreFileRenameRequirements;
        SingleSizeUnitToggle.IsOn = s.OnlyUseSingleUnit;
        SelectComboByTag(DefaultFileSystemComboBox, s.DataFileSystemType.ToString());
        SelectComboByTag(DefaultUdfVersionComboBox, s.DataUdfVersion.ToString());

        // Audio
        AutoCdTextToggle.IsOn = s.AudioAutoCDText;
        ShowAudioPlayerToggle.IsOn = s.AudioShowPlayer;
        SelectComboByTag(ReplayGainComboBox, ((int)s.AudioReplayGain).ToString());
        DefaultPauseBox.Value = Math.Clamp(s.AudioPauseLength, 0, 10);
        CrossfadeDurationBox.Value = Math.Clamp(s.AudioCrossfadingDuration, 0, 15);

        _loading = false;
    }

    private void SaveUiToSettings()
    {
        if (_loading) return;
        var s = AppServices.Current.Settings.Current;

        s.ShowNotifications = ShowNotificationsToggle.IsOn;
        s.ShowProgressNotifications = ShowProgressNotificationsToggle.IsOn;

        string prevLang = s.AppLanguage;
        s.AppLanguage = GetSelectedTag(LanguageComboBox);
        if (!string.Equals(prevLang, s.AppLanguage, StringComparison.OrdinalIgnoreCase))
        {
            LocalizationService.Current.SetLanguage(s.AppLanguage);
        }
        s.StartLastUsedProjectType = RememberLastProjectToggle.IsOn;
        s.DataAskForSettings = AskHowToBurnDataToggle.IsOn;
        s.ExitToNotify = ExitToTrayToggle.IsOn;
        s.UseCustomTempFolder = UseCustomTempToggle.IsOn;
        s.CustomTempFolder = CustomTempFolderBox.Text?.Trim() ?? string.Empty;
        CustomTempFolderBox.IsEnabled = s.UseCustomTempFolder;
        s.LoggingEnabled = LoggingEnabledToggle.IsOn;
        if (int.TryParse(GetSelectedTag(LoggingLevelComboBox), out int logLvl)) s.LoggingLevel = logLvl;
        BurningEngine.UpdateLastLogPath();

        s.DataLabel = string.IsNullOrWhiteSpace(DefaultDiscLabelBox.Text) ? "Disc" : DefaultDiscLabelBox.Text.Trim();
        s.DataCacheFilesAutomatically = AutoCacheRemovableToggle.IsOn;
        s.DataCacheNetworkFilesAutomatically = AutoCacheNetworkToggle.IsOn;
        s.DataUpdateAutomatically = AutoUpdateCompilationsToggle.IsOn;
        s.IgnoreFileRenameRequirements = IgnoreFileRenameToggle.IsOn;
        s.OnlyUseSingleUnit = SingleSizeUnitToggle.IsOn;
        if (int.TryParse(GetSelectedTag(DefaultFileSystemComboBox), out int fs)) s.DataFileSystemType = fs;
        if (int.TryParse(GetSelectedTag(DefaultUdfVersionComboBox), out int udf)) s.DataUdfVersion = udf;

        s.AudioAutoCDText = AutoCdTextToggle.IsOn;
        s.AudioShowPlayer = ShowAudioPlayerToggle.IsOn;
        if (int.TryParse(GetSelectedTag(ReplayGainComboBox), out int rg)) s.AudioReplayGain = (ReplayGainMode)rg;
        s.AudioPauseLength = Math.Clamp((int)DefaultPauseBox.Value, 0, 10);
        s.AudioCrossfadingDuration = Math.Clamp(CrossfadeDurationBox.Value, 0, 15);

        AppServices.Current.Settings.Save();
        AppServices.Current.ApplyDefaultDataBurnerSettings();
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var tag = GetSelectedTag(ThemeComboBox);
        var theme = tag switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
    }

    private void SettingControl_Changed(object sender, RoutedEventArgs e) => SaveUiToSettings();
    private void SettingText_Changed(object sender, TextChangedEventArgs e) => SaveUiToSettings();
    private void SettingNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => SaveUiToSettings();

    private void DefaultDiscLabelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDiscLabelPreview();
        SaveUiToSettings();
    }

    private void UpdateDiscLabelPreview()
    {
        var preview = DataCompilation.ReplaceDiscLabelVariables(DefaultDiscLabelBox.Text);
        DiscLabelHintText.Text = $"Variables: $date, $time, $year, $yr, $month, $day, $user  •  Preview: \"{preview}\"";
    }

    private async void BrowseTempFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await FilePickerHelper.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            CustomTempFolderBox.Text = folder;
            UseCustomTempToggle.IsOn = true;
            SaveUiToSettings();
        }
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

    private async void ImportLegacyIniButton_Click(object sender, RoutedEventArgs e)
    {
        var iniPath = await FilePickerHelper.PickSingleFileAsync([".ini"]);
        if (string.IsNullOrWhiteSpace(iniPath)) return;

        try
        {
            var imported = SettingsService.ImportLegacyIni(iniPath);
            var target = AppServices.Current.Settings.Current;
            foreach (var prop in typeof(AppSettings).GetProperties().Where(p => p.CanRead && p.CanWrite))
            {
                prop.SetValue(target, prop.GetValue(imported));
            }
            AppServices.Current.Settings.Save();
            LoadSettingsToUi();

            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "Settings Imported";
            StatusInfoBar.Message = $"Imported legacy settings from '{Path.GetFileName(iniPath)}'.";
            StatusInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Import Failed";
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
        }
    }

    private async void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        bool confirmed = await AppServices.Current.Dialogs.ConfirmAsync(
            XamlRoot,
            "Reset Settings",
            "Reset all CDBurner10 preferences back to factory defaults?",
            "Reset",
            "Cancel");
        if (!confirmed) return;

        AppServices.Current.Settings.ResetToDefaults();
        LoadSettingsToUi();

        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = "Defaults Restored";
        StatusInfoBar.Message = "All settings have been reset to their default values.";
        StatusInfoBar.IsOpen = true;
    }

    private void GitHubButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(GitHubUrl);
    }

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTheme = (App.MainWindow?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
        var dialog = new AboutDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = activeTheme,
            CornerRadius = new CornerRadius(8)
        };
        LocalizationService.Current.TranslateTree(dialog);
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
