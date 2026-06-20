using System;
using System.Runtime.InteropServices;
using Ghostty.Core.Accessibility;
using Ghostty.Core.Config;
using Microsoft.UI.Dispatching;

namespace Ghostty.Accessibility;

/// <summary>
/// Watches the Windows High Contrast state and palette and asks
/// ConfigService to layer (or clear) the HC color override. App-level,
/// single instance (config is app-wide). Mirrors WindowThemeManager's
/// lifecycle: subscribe in the ctor, unsubscribe in Dispose, marshal
/// background-thread events to the UI dispatcher.
/// </summary>
internal sealed partial class HighContrastMonitor : IDisposable
{
    // GetSysColor indices (winuser.h).
    private const int COLOR_WINDOW = 5;
    private const int COLOR_WINDOWTEXT = 8;
    private const int COLOR_HIGHLIGHT = 13;
    private const int COLOR_HIGHLIGHTTEXT = 14;

    [LibraryImport("user32.dll")]
    private static partial uint GetSysColor(int nIndex);

    private readonly IConfigService _configService;
    private readonly DispatcherQueue _dispatcher;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings;
    private bool _isDisposed;

    public HighContrastMonitor(IConfigService configService, DispatcherQueue dispatcher)
    {
        _configService = configService;
        _dispatcher = dispatcher;
        _uiSettings = new Windows.UI.ViewManagement.UISettings();

        // HC on/off changes the system palette, so ColorValuesChanged fires.
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        // The user toggling windows-high-contrast=false must re-evaluate too.
        _configService.ConfigChanged += OnConfigChanged;

        Apply();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        _configService.ConfigChanged -= OnConfigChanged;
    }

    private void OnColorValuesChanged(
        Windows.UI.ViewManagement.UISettings sender, object args)
        => _dispatcher.TryEnqueue(Apply);

    private void OnConfigChanged(IConfigService _)
        => _dispatcher.TryEnqueue(Apply);

    // Always runs on the UI thread (ctor + dispatched events).
    private void Apply()
    {
        if (_isDisposed) return;

        var shouldApply = HighContrastState.ShouldApply(
            HighContrastDetector.IsActive(),
            userOptOut: !_configService.WindowsHighContrast);

        if (!shouldApply)
        {
            _configService.SetHighContrastOverride(null);
            return;
        }

        var colors = new HighContrastColors(
            Background: GetSysColor(COLOR_WINDOW),
            Foreground: GetSysColor(COLOR_WINDOWTEXT),
            SelectionBackground: GetSysColor(COLOR_HIGHLIGHT),
            SelectionForeground: GetSysColor(COLOR_HIGHLIGHTTEXT));

        _configService.SetHighContrastOverride(HighContrastConfigWriter.Render(colors));
    }
}
