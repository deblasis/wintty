using Microsoft.UI.Xaml;

namespace Ghostty.Controls;

/// <summary>
/// AOT-safe resolver for WinUI 3 theme resources. Some system-level
/// resources (<c>TextFillColorPrimaryBrush</c>, <c>CaptionTextBlockFontSize</c>)
/// resolve to wrong values or fail silently under NativeAOT because
/// the Window-level theme context may not match <c>RequestedTheme="Dark"</c>.
///
/// This helper tries the runtime resource dictionary first and falls
/// back to an explicit default. When config-driven styling arrives,
/// the fallback values become the config-supplied values instead of
/// hardcoded constants.
/// </summary>
internal static class ThemeResources
{
    /// <summary>
    /// Try to resolve a named resource from the application's merged
    /// dictionaries. Returns <paramref name="fallback"/> if the key
    /// is missing or the value is not of type <typeparamref name="T"/>.
    /// </summary>
    public static T Get<T>(string key, T fallback)
    {
        try
        {
            if (Application.Current?.Resources?.TryGetValue(key, out var value) == true
                && value is T typed)
            {
                return typed;
            }
        }
        catch
        {
            // Resource lookup can throw during early init or after
            // teardown. Swallow and return fallback.
        }

        return fallback;
    }
}
