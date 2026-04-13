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

    public bool IsEnabled => _configService.ShellThemeEnabled;

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
        // Active tab uses cursor colors: cursor-color as background,
        // cursor-text as foreground. This matches the terminal cursor
        // appearance and creates a consistent visual accent.
        var newAccent = _configService.CursorColor.HasValue
            ? UnpackColor(_configService.CursorColor.Value)
            : FindAccent(palette);
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
    /// Pick an accent color from the ANSI palette. Prefers blue
    /// (index 4 / 12) as it's the most natural UI accent. Falls
    /// back to cyan, then the most saturated non-red color.
    /// </summary>
    private static Windows.UI.Color FindAccent(uint[] palette)
    {
        // Preference order: blue, bright blue, cyan, bright cyan.
        // These make the best UI accents across light/dark themes.
        int[] preferred = [4, 12, 6, 14];
        foreach (var i in preferred)
        {
            if (i < palette.Length && GetSaturation(UnpackColor(palette[i])) > 0.15f)
                return UnpackColor(palette[i]);
        }

        // Fallback: most saturated color, skipping black/white
        // (indices 0, 7, 8, 15) and reds (1, 9) which are too
        // aggressive for UI accent.
        float maxSat = 0f;
        int bestIdx = 4;
        int[] skip = [0, 1, 7, 8, 9, 15];
        for (int i = 0; i < 16; i++)
        {
            if (Array.IndexOf(skip, i) >= 0) continue;
            var sat = GetSaturation(UnpackColor(palette[i]));
            if (sat > maxSat) { maxSat = sat; bestIdx = i; }
        }

        return UnpackColor(palette[bestIdx]);
    }

    private static float GetSaturation(Windows.UI.Color c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        if (max == min) return 0f;
        float l = (max + min) / 2f;
        float d = max - min;
        return l > 0.5f ? d / (2f - max - min) : d / (max + min);
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
