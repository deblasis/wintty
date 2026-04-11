using System;
using System.Runtime.InteropServices;
using Ghostty.Core.Config;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Ghostty.Services;

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

    // System theme tracking for "system" mode.
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

    public WindowThemeManager(IConfigService configService, DispatcherQueue dispatcher)
    {
        _configService = configService;
        _dispatcher = dispatcher;
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
    public void ApplyToWindow(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        int useDarkMode = IsDarkMode ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,
            in useDarkMode, sizeof(int));
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
            // System theme changes only affect "system" mode. "Auto"
            // derives from the config background color, not the OS.
            if (_configService.WindowTheme != "system") return;

            var previous = IsDarkMode;
            Resolve();
            if (IsDarkMode != previous)
                ThemeChanged?.Invoke(IsDarkMode);
        });
    }

    private void Resolve()
    {
        IsDarkMode = _configService.WindowTheme switch
        {
            "light" => false,
            "dark" => true,
            "system" => IsSystemDark(),
            "auto" => IsBackgroundDark(),
            _ => true, // default to dark
        };
    }

    /// <summary>
    /// Check whether the OS is currently in dark mode by reading
    /// the foreground color from UISettings. White foreground means
    /// dark mode (light text on dark background).
    /// </summary>
    private bool IsSystemDark()
    {
        var fg = _uiSettings.GetColorValue(
            Windows.UI.ViewManagement.UIColorType.Foreground);
        return fg.R > 128;
    }

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

    // DWM interop
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, in int pvAttribute, int cbAttribute);
}
