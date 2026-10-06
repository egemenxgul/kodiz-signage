using System.Globalization;
using System.Windows;
using KodizSignage.Core.Models;

namespace KodizSignage.Services;

public interface ILocalizationService
{
    /// <summary>The effective language ("tr" or "en").</summary>
    string CurrentCode { get; }

    /// <summary>Raised after the string dictionary was swapped.</summary>
    event EventHandler? LanguageChanged;

    void Apply(AppLanguage language);

    string Get(string key);

    string Format(string key, params object[] args);
}

/// <summary>
/// Swaps a merged ResourceDictionary of strings at runtime. XAML uses DynamicResource, so the
/// UI updates live without restart.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private const string Marker = "Localization/Strings.";
    private ResourceDictionary? _current;

    public string CurrentCode { get; private set; } = "en";

    public event EventHandler? LanguageChanged;

    public static string Resolve(AppLanguage language) => language switch
    {
        AppLanguage.Turkish => "tr",
        AppLanguage.English => "en",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en",
    };

    public void Apply(AppLanguage language)
    {
        var code = Resolve(language);
        if (_current is not null && code == CurrentCode)
        {
            return;
        }

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/KodizSignage;component/Localization/Strings.{code}.xaml", UriKind.Absolute),
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains(Marker, StringComparison.OrdinalIgnoreCase) == true);
        if (old is not null)
        {
            merged.Remove(old);
        }

        merged.Add(dictionary);
        _current = dictionary;
        CurrentCode = code;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Get(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
