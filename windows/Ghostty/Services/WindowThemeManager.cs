using System;
using Ghostty.Core.Config;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using WinRT.Interop;

namespace Ghostty.Services;

/// <summary>
/// How the manager resolves <c>window-theme</c> values that aren't
/// explicitly <c>light</c> or <c>dark</c> (i.e. <c>auto</c>,
/// <c>ghostty</c>). MainWindow uses Palette so terminal chrome tracks
/// the active palette. SettingsWindow uses System so the config UI
/// feels OS-native regardless of the terminal's color scheme.
/// </summary>
internal enum ThemeFallbackStyle
{
    Palette,
    System,
}

/// <summary>
/// Maps the libghostty <c>window-theme</c> config value to WinUI 3
/// ElementTheme and the DWM immersive dark mode attribute. Handles
/// "light", "dark", "system" (follows OS), and "auto" (derives from
/// background color luminance, matching the macOS port).
///
/// Subscribe to <see cref="ThemeChanged"/> for live-reload updates.
/// </summary>
internal sealed class WindowThemeManager : IDisposable
{
    private readonly IConfigService _configService;
    private readonly DispatcherQueue _dispatcher;
    private readonly ThemeFallbackStyle _fallback;

    // System theme tracking for "system" mode (and any non-explicit
    // mode when the fallback is System).
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings;

    /// <summary>
    /// Fired on the UI thread whenever the resolved theme changes.
    /// The bool argument is true when dark mode is active.
    /// </summary>
    public event Action<bool>? ThemeChanged;

    /// <summary>Current resolved dark-mode state.</summary>
    public bool IsDarkMode { get; private set; }

    /// <summary>
    /// The ElementTheme to apply to the XAML root element.
    /// </summary>
    public ElementTheme ElementTheme => IsDarkMode ? ElementTheme.Dark : ElementTheme.Light;

    public WindowThemeManager(
        IConfigService configService,
        DispatcherQueue dispatcher,
        ThemeFallbackStyle fallback = ThemeFallbackStyle.Palette)
    {
        _configService = configService;
        _dispatcher = dispatcher;
        _fallback = fallback;
        _uiSettings = new Windows.UI.ViewManagement.UISettings();

        _configService.ConfigChanged += OnConfigChanged;
        _uiSettings.ColorValuesChanged += OnSystemThemeChanged;

        Resolve();
    }

    public void Dispose()
    {
        _configService.ConfigChanged -= OnConfigChanged;
        _uiSettings.ColorValuesChanged -= OnSystemThemeChanged;
    }

    /// <summary>
    /// Apply the current theme to a window's DWM non-client area.
    /// Must be called from the UI thread.
    /// </summary>
    public unsafe void ApplyToWindow(Window window)
    {
        var hwnd = new HWND(WindowNative.GetWindowHandle(window));
        BOOL useDarkMode = IsDarkMode;
        PInvoke.DwmSetWindowAttribute(
            hwnd,
            DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE,
            &useDarkMode,
            (uint)sizeof(BOOL));
    }

    private void OnConfigChanged(IConfigService _)
    {
        var previous = IsDarkMode;
        Resolve();
        if (IsDarkMode != previous)
            ThemeChanged?.Invoke(IsDarkMode);
    }

    private void OnSystemThemeChanged(
        Windows.UI.ViewManagement.UISettings sender, object args)
    {
        // ColorValuesChanged fires on a background thread.
        _dispatcher.TryEnqueue(() =>
        {
            // System theme flips only matter when the resolved mode
            // consults the OS. Explicit light/dark never do; "system"
            // always does; auto/ghostty do only when fallback=System.
            if (!TracksSystem(_configService.WindowTheme)) return;

            var previous = IsDarkMode;
            Resolve();
            if (IsDarkMode != previous)
                ThemeChanged?.Invoke(IsDarkMode);
        });
    }

    private bool TracksSystem(string windowTheme) => windowTheme switch
    {
        "light" or "dark" => false,
        "system" => true,
        _ => _fallback == ThemeFallbackStyle.System,
    };

    private void Resolve()
    {
        IsDarkMode = _configService.WindowTheme switch
        {
            "light" => false,
            "dark" => true,
            "system" => IsSystemDark(),
            // auto/ghostty (and any unknown value): consult the fallback.
            // Palette matches the terminal's chrome to the active palette;
            // System makes the window feel OS-native regardless.
            _ => _fallback == ThemeFallbackStyle.System
                ? IsSystemDark()
                : IsBackgroundDark(),
        };
    }

    /// <summary>
    /// Check whether the OS is currently in dark mode. Uses the
    /// shared <see cref="OsTheme"/> helper; we keep the
    /// <see cref="_uiSettings"/> instance for the ColorValuesChanged
    /// subscription but route the actual dark-mode read through the
    /// shared heuristic so it can't diverge from ConfigService.
    /// </summary>
    private static bool IsSystemDark() => OsTheme.IsDark();

    /// <summary>
    /// Derive theme from the terminal background color luminance,
    /// matching the macOS port's "auto" behavior. A background with
    /// relative luminance below 0.5 is considered dark.
    /// </summary>
    private bool IsBackgroundDark()
    {
        // BackgroundColor is packed 0x00RRGGBB.
        var color = _configService.BackgroundColor;
        var r = (color >> 16) & 0xFF;
        var g = (color >> 8) & 0xFF;
        var b = color & 0xFF;

        // Same luminance test the macOS port uses via NSColor.isLightColor:
        // relative luminance (BT.709 coefficients). A color is "light" if
        // luminance >= 0.5, matching the NSColor extension upstream.
        var luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        return luminance < 0.5;
    }

}
