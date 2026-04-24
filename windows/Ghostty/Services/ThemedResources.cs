using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Services;

/// <summary>
/// Helpers for resolving XAML resources against a specific element's
/// ActualTheme rather than Application.RequestedTheme.
///
/// Application.Current.Resources[key] and FrameworkElement.Resources[key]
/// both resolve ThemeDictionaries using Application.RequestedTheme. When
/// Application.RequestedTheme is pinned at launch (as it is here — see
/// App.xaml.cs), any control whose ActualTheme differs from the app
/// theme reads the wrong dictionary entry. The methods here walk the
/// theme dictionaries explicitly with the caller-supplied theme key so
/// lookups stay correct in mixed-theme scenes (issue # 325).
/// </summary>
internal static class ThemedResources
{
    /// <summary>
    /// Returns "Light" or "Default" (the WinUI key for dark/unthemed) for
    /// the given element theme. Callers use this to index into
    /// ResourceDictionary.ThemeDictionaries.
    /// </summary>
    public static string ThemeDictionaryKey(ElementTheme theme) =>
        theme == ElementTheme.Light ? "Light" : "Default";

    /// <summary>
    /// Look up a brush under <paramref name="key"/> in the supplied
    /// ResourceDictionary's theme dictionary matching
    /// <paramref name="theme"/>, walking merged dictionaries recursively.
    /// Returns false if the key is absent from every theme dict in the
    /// graph; callers can then fall back to the default app-theme lookup.
    /// </summary>
    public static bool TryFindBrush(
        ResourceDictionary resources,
        string key,
        ElementTheme theme,
        [NotNullWhen(true)] out Brush? brush)
    {
        var themeKey = ThemeDictionaryKey(theme);
        return TryFindBrushCore(resources, key, themeKey, out brush);
    }

    private static bool TryFindBrushCore(
        ResourceDictionary rd, string key, string themeKey,
        [NotNullWhen(true)] out Brush? brush)
    {
        if (rd.ThemeDictionaries.TryGetValue(themeKey, out var dictObj) &&
            dictObj is ResourceDictionary themeDict)
        {
            if (themeDict.TryGetValue(key, out var v) && v is Brush b)
            {
                brush = b;
                return true;
            }
            foreach (var m in themeDict.MergedDictionaries)
            {
                if (TryFindBrushCore(m, key, themeKey, out brush)) return true;
            }
        }
        foreach (var m in rd.MergedDictionaries)
        {
            if (TryFindBrushCore(m, key, themeKey, out brush)) return true;
        }
        brush = null;
        return false;
    }
}
