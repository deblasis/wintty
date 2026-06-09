using System;
using Ghostty.Core.Config;
using Ghostty.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

internal sealed partial class GeneralPage : Page
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;
    private bool _loading = true;

    /// <summary>
    /// Raised when the user flips the vertical-tabs toggle. MainWindow
    /// subscribes and runs the layout animation immediately so the
    /// window does not wait for the debounced config write +
    /// ConfigChanged round-trip.
    /// </summary>
    public static event Action<bool>? VerticalTabsToggled;

    public GeneralPage(IConfigService configService, IConfigFileEditor editor)
    {
        _configService = configService;
        _editor = editor;
        InitializeComponent();
        LoadValues();
        _loading = false;
    }

    private void LoadValues()
    {
        AutoReloadToggle.IsOn = _configService.AutoReloadEnabled;
        VerticalTabsToggle.IsOn = _configService.VerticalTabs;
        // undo-timeout is a Duration stored as milliseconds; present it
        // directly in ms (its native granularity) so a sub-second value is
        // never lost to a seconds round-trip.
        UndoTimeoutBox.Value = _configService.UndoTimeoutMs;
    }

    private void AutoReloadToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _configService.SuppressWatcher(true);
        try { _editor.SetValue("auto-reload-config", AutoReloadToggle.IsOn ? "true" : "false"); }
        finally { _configService.SuppressWatcher(false); }
        _configService.Reload();
    }

    private void VerticalTabsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = VerticalTabsToggle.IsOn;

        // Persistence is debounced so rapid toggling coalesces into a
        // single write. The animation fires immediately via the static
        // event so the UX does not lag behind the pointer while the
        // scheduler waits out its debounce window.
        Ghostty.App.ConfigWriteScheduler?.Schedule(
            "vertical-tabs", on ? "true" : "false");
        VerticalTabsToggled?.Invoke(on);
    }

    private void UndoTimeout_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        // A cleared/invalid NumberBox yields NaN; ignore it rather than
        // casting NaN to 0 and silently writing "0ms" (which disables undo).
        if (double.IsNaN(sender.Value)) return;

        var ms = Math.Max(0, (int)Math.Round(sender.Value));
        // Debounce through the scheduler rather than writing + Reload() on
        // every spinner tick (a held spin button fires many ValueChanged in a
        // row). The scheduler coalesces last-write-wins, try/catches the
        // SetValue, and owns the post-write reload — mirroring the
        // vertical-tabs toggle. "<n>ms" is a valid Duration unit; "0ms"
        // round-trips to a disabled undo policy.
        Ghostty.App.ConfigWriteScheduler?.Schedule("undo-timeout", ms + "ms");
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        _configService.Reload();
    }

    private void OpenFileButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _configService.ConfigFilePath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            StaticLoggers.GeneralPage.LogConfigOpenFailed(ex);
        }
    }
}

internal static partial class GeneralPageLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.SettingsUi.ConfigOpenFailed,
                   Level = LogLevel.Warning,
                   Message = "Failed to open config file")]
    internal static partial void LogConfigOpenFailed(
        this ILogger<GeneralPage> logger, System.Exception ex);
}
