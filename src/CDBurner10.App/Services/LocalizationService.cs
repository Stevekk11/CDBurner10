using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CDBurner10.App.Services;

/// <summary>
/// Instant runtime localization service for CDBurner10.
/// Supports English (default) plus German (de-DE), Czech (cs-CZ), Polish (pl-PL),
/// Slovak (sk-SK), and Spanish (es-ES). Translates both programmatic strings (Loc.T)
/// and live WinUI 3 visual trees losslessly without requiring an app restart.
/// </summary>
public sealed class LocalizationService
{
    public static LocalizationService Current { get; } = new();

    public static readonly string[] SupportedCultures =
    [
        "en-US",
        "de-DE",
        "cs-CZ",
        "pl-PL",
        "sk-SK",
        "es-ES"
    ];

    private sealed class TrackedText
    {
        public string Original { get; set; } = string.Empty;
        public string LastWritten { get; set; } = string.Empty;
    }

    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, TrackedText>> Originals = new();

    private Dictionary<string, string> _activeMap = new(StringComparer.Ordinal);
    private string _currentCulture = "en-US";

    public string CurrentCulture => _currentCulture;

    public event EventHandler? LanguageChanged;

    private LocalizationService() { }

    /// <summary>
    /// Resolves an AppSettings.AppLanguage tag ("", "de-DE", "cs-CZ", etc.) to one of the supported cultures.
    /// </summary>
    public static string ResolveCulture(string? configuredLanguage)
    {
        if (!string.IsNullOrWhiteSpace(configuredLanguage))
        {
            var match = MatchSupported(configuredLanguage.Trim());
            if (match is not null) return match;
        }

        var sys = CultureInfo.CurrentUICulture.Name;
        return MatchSupported(sys) ?? "en-US";
    }

    private static string? MatchSupported(string code)
    {
        foreach (var c in SupportedCultures)
        {
            if (c.Equals(code, StringComparison.OrdinalIgnoreCase))
                return c;
        }

        string twoLetter = code.Length >= 2 ? code[..2].ToLowerInvariant() : code.ToLowerInvariant();
        return twoLetter switch
        {
            "de" => "de-DE",
            "cs" => "cs-CZ",
            "pl" => "pl-PL",
            "sk" => "sk-SK",
            "es" => "es-ES",
            "en" => "en-US",
            _ => null
        };
    }

    public void SetLanguage(string? configuredLanguage)
    {
        string resolved = ResolveCulture(configuredLanguage);
        _currentCulture = resolved;

        try
        {
            var ci = new CultureInfo(resolved);
            CultureInfo.DefaultThreadCurrentUICulture = ci;
            CultureInfo.CurrentUICulture = ci;
        }
        catch { }

        _activeMap = LoadDictionary(resolved);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Dictionary<string, string> LoadDictionary(string culture)
    {
        if (culture.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Strings", $"{culture}.json");
            if (!File.Exists(path))
                return new Dictionary<string, string>(StringComparer.Ordinal);

            string json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is not null)
                return new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            // Fall back cleanly to English if a locale file is unreadable
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Translates an English source string into the currently active language.
    /// Falls back to the original English string if no translation exists.
    /// </summary>
    public string Translate(string? englishText)
    {
        if (string.IsNullOrEmpty(englishText)) return englishText ?? string.Empty;
        if (_activeMap.Count == 0) return englishText;

        if (_activeMap.TryGetValue(englishText, out var translated) && !string.IsNullOrEmpty(translated))
            return translated;

        string trimmed = englishText.Trim();
        if (trimmed != englishText && _activeMap.TryGetValue(trimmed, out var trimmedTrans) && !string.IsNullOrEmpty(trimmedTrans))
            return trimmedTrans;

        return englishText;
    }

    /// <summary>
    /// Recursively translates a WinUI 3 visual tree (including collapsed Expanders, ComboBoxItems,
    /// NavigationViewItems, RadioButtons, headers, placeholders, and tooltips) losslessly.
    /// </summary>
    public void TranslateTree(DependencyObject? root)
    {
        if (root is null) return;

        TranslateElement(root);

        // Traverse logical children that may not be in the VisualTree yet (collapsed Expanders, closed ComboBoxes, NavView menus)
        if (root is NavigationView nav)
        {
            foreach (var item in nav.MenuItems)
            {
                if (item is DependencyObject dep) TranslateTree(dep);
            }
            foreach (var item in nav.FooterMenuItems)
            {
                if (item is DependencyObject dep) TranslateTree(dep);
            }
            if (nav.Content is DependencyObject navContent)
                TranslateTree(navContent);
        }

        if (root is Expander expander)
        {
            if (expander.Header is DependencyObject headerDep) TranslateTree(headerDep);
            if (expander.Content is DependencyObject contentDep) TranslateTree(contentDep);
        }

        if (root is ItemsControl itemsControl)
        {
            foreach (var item in itemsControl.Items)
            {
                if (item is DependencyObject dep) TranslateTree(dep);
            }
        }

        if (root is RadioButtons radioButtons)
        {
            foreach (var item in radioButtons.Items)
            {
                if (item is DependencyObject dep) TranslateTree(dep);
            }
        }

        if (root is ContentControl cc && cc.Content is DependencyObject ccDep && root is not NavigationView && root is not Expander)
        {
            TranslateTree(ccDep);
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            TranslateTree(VisualTreeHelper.GetChild(root, i));
        }
    }

    private void TranslateElement(DependencyObject obj)
    {
        // ToolTip
        if (ToolTipService.GetToolTip(obj) is string tip && !string.IsNullOrWhiteSpace(tip))
        {
            if (TryTranslateTracked(obj, "ToolTip", tip, out var translated))
                ToolTipService.SetToolTip(obj, translated);
        }

        switch (obj)
        {
            case TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text):
                if (TryTranslateTracked(tb, "Text", tb.Text, out var tbText))
                    tb.Text = tbText;
                break;

            case NavigationViewItem nvi when nvi.Content is string s && !string.IsNullOrWhiteSpace(s):
                if (TryTranslateTracked(nvi, "Content", s, out var nviText))
                    nvi.Content = nviText;
                break;

            case NavigationViewItemHeader nvih when nvih.Content is string s && !string.IsNullOrWhiteSpace(s):
                if (TryTranslateTracked(nvih, "Content", s, out var nvihText))
                    nvih.Content = nvihText;
                break;

            case ToggleSwitch ts:
                if (ts.Header is string tsh && !string.IsNullOrWhiteSpace(tsh) && TryTranslateTracked(ts, "Header", tsh, out var tsText))
                    ts.Header = tsText;
                break;

            case TextBox textBox:
                if (textBox.Header is string tbh && !string.IsNullOrWhiteSpace(tbh) && TryTranslateTracked(textBox, "Header", tbh, out var tbhText))
                    textBox.Header = tbhText;
                if (!string.IsNullOrWhiteSpace(textBox.PlaceholderText) && TryTranslateTracked(textBox, "Placeholder", textBox.PlaceholderText, out var tbpText))
                    textBox.PlaceholderText = tbpText;
                break;

            case ComboBox combo:
                if (combo.Header is string cbh && !string.IsNullOrWhiteSpace(cbh) && TryTranslateTracked(combo, "Header", cbh, out var cbhText))
                    combo.Header = cbhText;
                if (!string.IsNullOrWhiteSpace(combo.PlaceholderText) && TryTranslateTracked(combo, "Placeholder", combo.PlaceholderText, out var cbpText))
                    combo.PlaceholderText = cbpText;
                break;

            case NumberBox nb:
                if (nb.Header is string nbh && !string.IsNullOrWhiteSpace(nbh) && TryTranslateTracked(nb, "Header", nbh, out var nbhText))
                    nb.Header = nbhText;
                if (!string.IsNullOrWhiteSpace(nb.PlaceholderText) && TryTranslateTracked(nb, "Placeholder", nb.PlaceholderText, out var nbpText))
                    nb.PlaceholderText = nbpText;
                break;

            case RadioButtons rbs:
                if (rbs.Header is string rbh && !string.IsNullOrWhiteSpace(rbh) && TryTranslateTracked(rbs, "Header", rbh, out var rbhText))
                    rbs.Header = rbhText;
                break;

            case ContentDialog dlg:
                if (dlg.Title is string dt && !string.IsNullOrWhiteSpace(dt) && TryTranslateTracked(dlg, "Title", dt, out var dtText))
                    dlg.Title = dtText;
                if (!string.IsNullOrWhiteSpace(dlg.PrimaryButtonText) && TryTranslateTracked(dlg, "Primary", dlg.PrimaryButtonText, out var dpText))
                    dlg.PrimaryButtonText = dpText;
                if (!string.IsNullOrWhiteSpace(dlg.SecondaryButtonText) && TryTranslateTracked(dlg, "Secondary", dlg.SecondaryButtonText, out var dsText))
                    dlg.SecondaryButtonText = dsText;
                if (!string.IsNullOrWhiteSpace(dlg.CloseButtonText) && TryTranslateTracked(dlg, "Close", dlg.CloseButtonText, out var dcText))
                    dlg.CloseButtonText = dcText;
                break;

            case ContentControl cc when cc.Content is string s && !string.IsNullOrWhiteSpace(s):
                // Skip language selector items so native language names stay readable
                if (cc is ComboBoxItem cbi && cbi.Tag is string tag &&
                    (tag is "en-US" or "de-DE" or "cs-CZ" or "pl-PL" or "sk-SK" or "es-ES"))
                    break;
                if (TryTranslateTracked(cc, "Content", s, out var ccText))
                    cc.Content = ccText;
                break;
        }
    }

    /// <summary>
    /// Translates a UI property while preserving dynamic code-behind updates:
    /// if the element's current value differs from what LocalizationService last wrote,
    /// code-behind updated it dynamically (e.g. MediaStatusText or DiscCapacityBar), so we
    /// refresh the tracked original instead of reverting to a stale initial XAML placeholder.
    /// </summary>
    private bool TryTranslateTracked(DependencyObject obj, string propKey, string currentValue, out string result)
    {
        var dict = Originals.GetOrCreateValue(obj);
        if (!dict.TryGetValue(propKey, out var entry))
        {
            entry = new TrackedText { Original = currentValue, LastWritten = currentValue };
            dict[propKey] = entry;
        }
        else if (!string.Equals(currentValue, entry.LastWritten, StringComparison.Ordinal))
        {
            // Code-behind updated this property dynamically since we last translated it
            entry.Original = currentValue;
        }

        result = Translate(entry.Original);
        entry.LastWritten = result;
        return !string.Equals(currentValue, result, StringComparison.Ordinal);
    }
}

public static class Loc
{
    public static string T(string englishText) => LocalizationService.Current.Translate(englishText);
}
