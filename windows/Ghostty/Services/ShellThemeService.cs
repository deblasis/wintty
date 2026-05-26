using System;

namespace Ghostty.Services;

/// <summary>
/// Derives a UI color scheme from the terminal's ANSI palette.
/// Fires <see cref="ThemeChanged"/> when the derived colors change
/// so UI controls can update.
/// </summary>
internal sealed class ShellThemeService
{
    private readonly ConfigService _configService;

    public event Action? ThemeChanged;

    public Windows.UI.Color TitleBarBackground { get; private set; }
    public Windows.UI.Color TitleBarForeground { get; private set; }
    public Windows.UI.Color TabBarBackground { get; private set; }
    public Windows.UI.Color AccentColor { get; private set; }
    public Windows.UI.Color ActiveTabText { get; private set; }
    public Windows.UI.Color InactiveTabText { get; private set; }
    public Windows.UI.Color ScrollbarTrack { get; private set; }
    public Windows.UI.Color ScrollbarThumb { get; private set; }

    public bool IsEnabled => string.Equals(_configService.WindowTheme, "ghostty", StringComparison.OrdinalIgnoreCase);

    private bool _wasEnabled;

    internal ShellThemeService(ConfigService configService)
    {
        _configService = configService;
        _wasEnabled = IsEnabled;
        Recompute();
        configService.ConfigChanged += _ =>
        {
            if (Recompute()) ThemeChanged?.Invoke();
        };
    }

    /// <summary>
    /// Recompute derived colors from the current palette.
    /// Returns true if any color changed or the enabled state flipped.
    /// </summary>
    private bool Recompute()
    {
        var enabled = IsEnabled;
        if (!enabled)
        {
            // Report change when transitioning from enabled to disabled
            // so MainWindow can revert to standard chrome.
            var wasEnabled = _wasEnabled;
            _wasEnabled = false;
            return wasEnabled;
        }
        _wasEnabled = true;

        var bg = UnpackColor(_configService.BackgroundColor);
        var fg = UnpackColor(_configService.ForegroundColor);
        var palette = _configService.AnsiPalette;

        var newTitleBarBg = bg;
        var newTitleBarFg = fg;
        var newTabBarBg = ShiftBrightness(bg, fg, 0.05f);
        // Chrome accent: accent-color when set, otherwise inherit
        // cursor-color (the original "cursor matches chrome" intent).
        // cursor-color is always populated by ConfigService (foreground
        // fallback), so there's no third tier to worry about.
        var newAccent = UnpackColor(
            _configService.AccentColor
            ?? _configService.CursorColor
            ?? _configService.ForegroundColor);
        var newActiveTabText = _configService.CursorTextColor.HasValue
            ? UnpackColor(_configService.CursorTextColor.Value)
            : bg; // fallback: background color on cursor-color bg
        var newInactiveTabText = UnpackColor(palette[8]); // bright black
        var newScrollbarTrack = ShiftBrightness(bg, fg, 0.08f);
        var newScrollbarThumb = Windows.UI.Color.FromArgb(
            102, fg.R, fg.G, fg.B); // 40% opacity

        bool changed = TitleBarBackground != newTitleBarBg
            || TitleBarForeground != newTitleBarFg
            || TabBarBackground != newTabBarBg
            || AccentColor != newAccent
            || ActiveTabText != newActiveTabText
            || InactiveTabText != newInactiveTabText
            || ScrollbarTrack != newScrollbarTrack
            || ScrollbarThumb != newScrollbarThumb;

        TitleBarBackground = newTitleBarBg;
        TitleBarForeground = newTitleBarFg;
        TabBarBackground = newTabBarBg;
        AccentColor = newAccent;
        ActiveTabText = newActiveTabText;
        InactiveTabText = newInactiveTabText;
        ScrollbarTrack = newScrollbarTrack;
        ScrollbarThumb = newScrollbarThumb;

        return changed;
    }

    /// <summary>
    /// Shift a color slightly toward another color by the given amount.
    /// Used to derive tab bar and scrollbar track from background.
    /// </summary>
    private static Windows.UI.Color ShiftBrightness(
        Windows.UI.Color from, Windows.UI.Color toward, float amount)
    {
        return Windows.UI.Color.FromArgb(0xFF,
            (byte)Math.Clamp(from.R + (toward.R - from.R) * amount, 0f, 255f),
            (byte)Math.Clamp(from.G + (toward.G - from.G) * amount, 0f, 255f),
            (byte)Math.Clamp(from.B + (toward.B - from.B) * amount, 0f, 255f));
    }

    private static Windows.UI.Color UnpackColor(uint packed)
    {
        return Windows.UI.Color.FromArgb(0xFF,
            (byte)(packed >> 16),
            (byte)(packed >> 8),
            (byte)packed);
    }
}
