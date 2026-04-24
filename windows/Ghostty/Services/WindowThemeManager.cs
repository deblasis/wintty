using System;
using Ghostty.Core.Config;
using Ghostty.Core.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using WinRT.Interop;

namespace Ghostty.Services;

/// <summary>
/// Maps the libghostty <c>window-theme</c> config value to WinUI 3
/// ElementTheme and the DWM immersive dark mode attribute. Handles
/// "light", "dark", "system" (follows OS), and "auto" (derives from
/// background color luminance, matching the macOS port).
///
/// Subscribe to <see cref="ThemeChanged"/> for live-reload updates.
/// Resolution logic lives in
/// <see cref="Ghostty.Core.Windows.ThemeResolution"/> so it can be
/// unit-tested without a WinUI runtime.
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

    // ConfigService.ConfigChanged can be raised synchronously from a
    // non-UI thread (ThemePreviewService.ApplyThemeColors invokes it
    // inline; the reload path already marshals, but we can't rely on
    // every callsite doing so). Route through the dispatcher so
    // ThemeChanged subscribers — which touch XAML properties — always
    // run on the UI thread.
    private void OnConfigChanged(IConfigService _) =>
        _dispatcher.TryEnqueue(ResolveAndNotifyIfChanged);

    private void OnSystemThemeChanged(
        Windows.UI.ViewManagement.UISettings sender, object args)
    {
        // ColorValuesChanged fires on a background thread. System theme
        // flips only matter when the resolved mode consults the OS;
        // skip the dispatch otherwise.
        if (!ThemeResolution.TracksSystem(_configService.WindowTheme, _fallback)) return;
        _dispatcher.TryEnqueue(ResolveAndNotifyIfChanged);
    }

    private void ResolveAndNotifyIfChanged()
    {
        var previous = IsDarkMode;
        Resolve();
        if (IsDarkMode != previous)
            ThemeChanged?.Invoke(IsDarkMode);
    }

    private void Resolve()
    {
        IsDarkMode = ThemeResolution.ResolveIsDark(
            _configService.WindowTheme,
            _configService.BackgroundColor,
            _fallback,
            OsTheme.IsDark());
    }
}
