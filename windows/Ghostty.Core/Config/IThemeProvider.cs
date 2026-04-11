using System.Collections.Generic;

namespace Ghostty.Core.Config;

/// <summary>
/// Provides resolved theme values from config. Colors are
/// represented as uint (ARGB) to avoid WinUI dependencies
/// in Ghostty.Core.
/// </summary>
public interface IThemeProvider
{
    uint BackgroundColor { get; }
    uint ForegroundColor { get; }
    uint CursorColor { get; }
    uint SelectionColor { get; }
    double BackgroundOpacity { get; }
    string? FontFamily { get; }
    double FontSize { get; }
    string? ThemeName { get; }

    /// <summary>Available theme names (bundled + user).</summary>
    IReadOnlyList<string> AvailableThemes { get; }
}
