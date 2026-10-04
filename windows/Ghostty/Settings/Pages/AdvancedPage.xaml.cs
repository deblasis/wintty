using System;
using Ghostty.Core.Config;
using Ghostty.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

internal sealed partial class AdvancedPage : Page
{
    private readonly IConfigService _configService;
    private readonly ConfigService? _cs;
    private bool _loading = true;

    public AdvancedPage(IConfigService configService, IConfigFileEditor editor)
    {
        _ = editor;
        _configService = configService;
        _cs = configService as ConfigService;
        InitializeComponent();
        LoadValues();
        _loading = false;

        // Subscribe in Loaded rather than the ctor: SettingsWindow caches and
        // reuses page instances, so the ctor runs once while Loaded/Unloaded
        // fire on every navigation. A ctor-time subscription paired with an
        // Unloaded unsubscribe would be dropped the first time the user
        // navigates away and never restored on return.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
        => _configService.ConfigChanged += OnConfigChanged;

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => _configService.ConfigChanged -= OnConfigChanged;

    /// <summary>
    /// An external config change moves every value on this page, and the
    /// controls have to follow it.
    ///
    /// The two boxes here also keep the "what this page last wrote" markers
    /// their LostFocus handlers compare against, and LoadValues moves those
    /// with the boxes. Leaving them would be the quieter half of the same
    /// bug: the display would be right and the guard wrong, so the next blur
    /// would read as unchanged and suppress a write the user asked for.
    ///
    /// The guard is load-bearing, not tidy: assigning a control fires that
    /// control's own handler, and every handler here writes the key the
    /// control is showing.
    /// </summary>
    private void OnConfigChanged(IConfigService _)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            LoadValues();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Put every control on this page in step with the config, and with the
    /// markers the blur handlers compare against.
    ///
    /// Called from the constructor with <c>_loading</c> still true and again
    /// from <see cref="OnConfigChanged"/> under the same guard, so the two
    /// paths cannot describe different values.
    /// </summary>
    private void LoadValues()
    {
        HighContrastToggle.IsOn = _configService.WindowsHighContrast;
        SelectComboByTag(LogLevelCombo, _configService.LogLevel);
        LogFilterBox.Text = _configService.LogFilter ?? string.Empty;
        _logFilterWritten = LogFilterBox.Text.Trim();

        if (_cs is null) return;

        SingleInstanceToggle.IsOn = WindowsOnlyKeyParsers.ParseBool(
            _cs.GetRawFileValue("windows-single-instance"),
            defaultValue: false);

        var quake = _cs.GetRawFileValue("quick-terminal-key");
        QuakeKeyBox.Text = string.IsNullOrWhiteSpace(quake) ? string.Empty : quake;
        _quakeKeyWritten = QuakeKeyBox.Text.Trim();
    }

    // Blur is not an edit: it fires on tab-through, on a click elsewhere, on
    // the window closing. Writing unconditionally meant every pass through this
    // page rewrote both keys, and for a file with no quick-terminal-key line
    // SetValue APPENDS, so simply visiting Advanced materialised
    // `quick-terminal-key = ` that the user never set.
    //
    // Seeded values are trimmed to match what the handlers write, or a file
    // value with trailing space would look changed on the first blur.
    //
    // They are the value in the FILE, not the value last handed to the
    // scheduler: WriteDebounced moves them only once the debounced write is
    // known to have landed, which is the difference between "we asked" and
    // "it is saved".
    private string _quakeKeyWritten = string.Empty;
    private string _logFilterWritten = string.Empty;

    private void SingleInstanceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Ghostty.App.ConfigWriteScheduler?.Schedule(
            "windows-single-instance",
            SingleInstanceToggle.IsOn ? "true" : "false");
    }

    private void HighContrastToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Ghostty.App.ConfigWriteScheduler?.Schedule(
            "windows-high-contrast",
            HighContrastToggle.IsOn ? "true" : "false");
    }

    private void QuakeKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var raw = QuakeKeyBox.Text?.Trim() ?? string.Empty;
        if (raw == _quakeKeyWritten) return;
        WriteDebounced("quick-terminal-key", raw, () => _quakeKeyWritten = raw);
    }

    private void LogLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item)
            return;
        Ghostty.App.ConfigWriteScheduler?.Schedule(
            "log-level",
            item.Tag?.ToString() ?? "info");
    }

    private void LogFilterBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var filter = LogFilterBox.Text?.Trim() ?? string.Empty;
        if (filter == _logFilterWritten) return;
        WriteDebounced("log-filter", filter, () => _logFilterWritten = filter);
    }

    /// <summary>
    /// Queue a debounced write and move <paramref name="onWritten"/> only once
    /// the scheduler says the value reached the file.
    /// </summary>
    /// <remarks>
    /// The scheduler swallows a disk failure and logs it, so a blur that
    /// advanced the guard here advanced it for a write that never happened:
    /// typing the same value again read as unchanged and was suppressed, and
    /// the box looked saved while the file still held the old one. The
    /// per-key outcome is the scheduler's answer to that, so the guard is
    /// driven by the write and not by the intent.
    /// <para>
    /// A value that was replaced by a later edit of the same key, or that was
    /// queued to a scheduler already disposed, reports
    /// <see cref="ConfigWriteOutcome.Superseded"/> -- not a failure, but not a
    /// landing either, so the guard stays where it was and the edit that
    /// replaced it owns the answer. Nothing to do with either: the reload
    /// signal still fired, so the runtime state is right.
    /// </para>
    /// <para>
    /// The callback runs on the scheduler's thread, so the guard moves on the
    /// dispatcher with everything else this page touches.
    /// </para>
    /// </remarks>
    private void WriteDebounced(string key, string value, Action onWritten)
    {
        if (Ghostty.App.ConfigWriteScheduler is not { } scheduler)
        {
            // No scheduler to report from (a host that never created one).
            // Nothing was queued either, so the old unconditional advance is
            // the only answer available; freezing the box instead would make
            // the setting un-editable on that host.
            onWritten();
            return;
        }

        scheduler.Schedule(key, value, outcome =>
        {
            if (outcome != ConfigWriteOutcome.Written) return;
            DispatcherQueue.TryEnqueue(() => onWritten());
        });
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 2; // info
    }
}
