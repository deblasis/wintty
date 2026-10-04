using System;
using Ghostty.Controls.Settings;
using Ghostty.Core.Config;
using Ghostty.Core.Settings;
using Ghostty.Logging;
using Ghostty.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

internal sealed partial class ColorsPage : Page
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;
    private readonly SettingsConfigWriter _writer;
    private readonly SearchableList _themeList;
    private readonly SearchableList _lightThemeList;
    private readonly SearchableList _darkThemeList;
    private bool _loading = true;

    public ColorsPage(IConfigService configService, IConfigFileEditor editor, IThemeProvider theme)
    {
        _configService = configService;
        _editor = editor;
        _writer = new SettingsConfigWriter(configService, StaticLoggers.SettingsConfigWriter);
        InitializeComponent();

        _themeList = new SearchableList(ThemeSearch, chosen => OnThemeChosen(chosen));
        // Both pair boxes funnel into one handler that reads BOTH of them, so
        // committing either half writes the pair. SearchableList only calls
        // this on Enter or a click now, not on every arrow key the user browses
        // past, which is what used to rewrite theme on each keypress.
        _lightThemeList = new SearchableList(LightThemeSearch, _ => OnPairThemeChosen());
        _darkThemeList = new SearchableList(DarkThemeSearch, _ => OnPairThemeChosen());

        var themes = theme.AvailableThemes;
        _themeList.SetItems(themes);
        _lightThemeList.SetItems(themes);
        _darkThemeList.SetItems(themes);

        // Determine initial mode from current config and seed the
        // color pickers only for keys the user has actually overridden
        // in their config file. Inherited theme/default colors should
        // read as "unset" in the UI so the user can tell at a glance
        // whether they're customizing or accepting the theme.
        if (configService is ConfigService cs)
        {
            SyncColorOverride("foreground", ForegroundPicker, ForegroundResetButton,
                () => Rgb.FromRgb24(cs.ForegroundColor).ToHex());
            SyncColorOverride("background", BackgroundPicker, BackgroundResetButton,
                () => Rgb.FromRgb24(cs.BackgroundColor).ToHex());
            SyncColorOverride("cursor-color", CursorColorPicker, CursorColorResetButton,
                () => cs.CursorColor is uint cursor ? Rgb.FromRgb24(cursor).ToHex() : "");
            // selection-background has no typed accessor on ConfigService,
            // so parse the user's raw file value directly. Falls back to
            // empty if the entry is present but unparseable -- same UX
            // as "unset" -- rather than crashing the settings page.
            SyncColorOverride("selection-background", SelectionColorPicker, SelectionColorResetButton,
                () => ThemeParser.TryParseHexRgb(cs.GetRawFileValue("selection-background"), out var packed)
                    ? Rgb.FromRgb24(packed).ToHex()
                    : "");
            SyncColorOverride("accent-color", AccentColorPicker, AccentColorResetButton,
                () => cs.AccentColor is uint accent ? Rgb.FromRgb24(accent).ToHex() : "");

            SeedThemePickers(cs);
        }

        _loading = false;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Put the mode radios, the three boxes and the cards that hold them in
    /// step with the config, and write nothing.
    ///
    /// Callers own the <c>_loading</c> guard. That guard is load-bearing
    /// here and not merely tidy: checking a radio fires
    /// <see cref="ThemeMode_Changed"/>, which writes <c>theme</c> from
    /// whatever the boxes hold -- and mid-re-seed they hold a mixture of the
    /// old and the new value. The pair mode is the worse case, because the
    /// chain reads BOTH boxes and writes <c>light:..,dark:..</c> from them, so
    /// a half-applied re-seed writes a pair the user never chose.
    ///
    /// Both directions are set explicitly rather than only the one the config
    /// selects. A single-mode file behind a pair-mode UI is what made the
    /// first nudge on this page destructive: the pair boxes still read as the
    /// user's selection, and one commit rewrote <c>theme</c> as a pair.
    /// </summary>
    private void SeedThemePickers(ConfigService cs)
    {
        var pair = cs.LightTheme is not null && cs.DarkTheme is not null;

        SingleModeRadio.IsChecked = !pair;
        PairModeRadio.IsChecked = pair;

        SingleThemeCard.Visibility = pair ? Visibility.Collapsed : Visibility.Visible;
        LightThemeCard.Visibility = pair ? Visibility.Visible : Visibility.Collapsed;
        DarkThemeCard.Visibility = pair ? Visibility.Visible : Visibility.Collapsed;

        // Every box is written in both modes, not just the visible ones. The
        // hidden box is what ThemeMode_Changed seeds the other from when the
        // user flips the mode, so leaving a stale name in it hands them a
        // theme they removed.
        ThemeSearch.Text = pair ? string.Empty : cs.CurrentTheme;
        LightThemeSearch.Text = pair ? cs.LightTheme : string.Empty;
        DarkThemeSearch.Text = pair ? cs.DarkTheme : string.Empty;
    }

    private void ThemeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        // Route by the specific radio that became checked rather than
        // falling through a two-branch if/else. Both radios share this
        // handler, so adding a third ThemeMode option later (or wiring
        // Unchecked) doesn't silently pick the wrong branch; unrelated
        // senders fall out here cleanly.
        if (sender is not RadioButton { IsChecked: true } rb) return;

        if (rb == PairModeRadio)
        {
            // Switching to pair mode: seed both boxes from the current
            // single theme so the user sees their selection carried over.
            var current = ThemeSearch.Text.Trim();
            if (!string.IsNullOrEmpty(current))
            {
                LightThemeSearch.Text = current;
                DarkThemeSearch.Text = current;
            }

            SingleThemeCard.Visibility = Visibility.Collapsed;
            LightThemeCard.Visibility = Visibility.Visible;
            DarkThemeCard.Visibility = Visibility.Visible;
        }
        else if (rb == SingleModeRadio)
        {
            // Switching to single mode: pick the dark theme as default
            // (most users run dark mode), falling back to light.
            var fallback = DarkThemeSearch.Text.Trim();
            if (string.IsNullOrEmpty(fallback))
                fallback = LightThemeSearch.Text.Trim();

            SingleThemeCard.Visibility = Visibility.Visible;
            LightThemeCard.Visibility = Visibility.Collapsed;
            DarkThemeCard.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrEmpty(fallback))
            {
                ThemeSearch.Text = fallback;
                OnValueChanged("theme", fallback);
            }
        }
    }

    private void OnThemeChosen(string theme)
    {
        OnValueChanged("theme", theme);
    }

    private void OnPairThemeChosen()
    {
        var light = LightThemeSearch.Text.Trim();
        var dark = DarkThemeSearch.Text.Trim();

        // Need both to write a pair.
        if (string.IsNullOrEmpty(light) || string.IsNullOrEmpty(dark))
            return;

        // If both are the same, collapse to single theme.
        if (string.Equals(light, dark, StringComparison.OrdinalIgnoreCase))
        {
            OnValueChanged("theme", light);
            return;
        }

        OnValueChanged("theme", $"light:{light},dark:{dark}");
    }

    private void OnValueChanged(string key, string value)
    {
        if (_loading) return;
        _writer.Write(() => _editor.SetValue(key, value), key);
    }

    private void Foreground_ColorChanged(object? sender, string hex)
    {
        OnValueChanged("foreground", hex);
        ForegroundResetButton.Visibility = Visibility.Visible;
    }

    private void Background_ColorChanged(object? sender, string hex)
    {
        OnValueChanged("background", hex);
        BackgroundResetButton.Visibility = Visibility.Visible;
    }

    private void CursorColor_ColorChanged(object? sender, string hex)
    {
        OnValueChanged("cursor-color", hex);
        CursorColorResetButton.Visibility = Visibility.Visible;
    }

    private void SelectionColor_ColorChanged(object? sender, string hex)
    {
        OnValueChanged("selection-background", hex);
        SelectionColorResetButton.Visibility = Visibility.Visible;
    }

    private void AccentColor_ColorChanged(object? sender, string hex)
    {
        OnValueChanged("accent-color", hex);
        AccentColorResetButton.Visibility = Visibility.Visible;
    }

    private void Foreground_Reset(object sender, RoutedEventArgs e)
        => ResetColorOverride("foreground", ForegroundPicker, ForegroundResetButton);

    private void Background_Reset(object sender, RoutedEventArgs e)
        => ResetColorOverride("background", BackgroundPicker, BackgroundResetButton);

    private void CursorColor_Reset(object sender, RoutedEventArgs e)
        => ResetColorOverride("cursor-color", CursorColorPicker, CursorColorResetButton);

    private void SelectionColor_Reset(object sender, RoutedEventArgs e)
        => ResetColorOverride("selection-background", SelectionColorPicker, SelectionColorResetButton);

    private void AccentColor_Reset(object sender, RoutedEventArgs e)
        => ResetColorOverride("accent-color", AccentColorPicker, AccentColorResetButton);

    // Drop the override key from the config file, then clear the picker
    // and hide the reset button so the row reads as "no override set".
    // Suppressing the watcher keeps the file-change event from racing
    // the explicit Reload below; setting Color under the loading guard
    // blocks the picker's ColorChanged handler from firing a stray
    // OnValueChanged write back to disk.
    private void ResetColorOverride(string key, ColorPickerControl picker, Button resetButton)
    {
        if (_loading) return;
        _writer.Write(() => _editor.RemoveValue(key), key);

        _loading = true;
        try { picker.Color = ""; }
        finally { _loading = false; }
        resetButton.Visibility = Visibility.Collapsed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _configService.ConfigChanged += OnConfigChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _configService.ConfigChanged -= OnConfigChanged;
    }

    // External config edits (Raw Editor in this dialog, FSW auto-reload,
    // direct file edit) update _configService but won't otherwise refresh
    // what this page shows. SettingsWindow caches page instances, so what the
    // constructor seeded describes the config as of whenever the page was
    // built -- and a control left behind writes THAT value on the next nudge,
    // silently undoing the edit. AppearancePage and RawEditorPage subscribe
    // to ConfigChanged for the same reason.
    private void OnConfigChanged(IConfigService cs)
    {
        if (cs is not ConfigService impl) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            // Guard against the page having been detached between the event
            // fire and this lambda running -- e.g., navigated away mid-cycle.
            // SyncColorOverride on a detached control is harmless but a
            // wasted dispatch.
            if (!IsLoaded) return;
            _loading = true;
            try
            {
                SyncColorOverride("foreground", ForegroundPicker, ForegroundResetButton,
                    () => Rgb.FromRgb24(impl.ForegroundColor).ToHex());
                SyncColorOverride("background", BackgroundPicker, BackgroundResetButton,
                    () => Rgb.FromRgb24(impl.BackgroundColor).ToHex());
                SyncColorOverride("cursor-color", CursorColorPicker, CursorColorResetButton,
                    () => impl.CursorColor is uint cursor ? Rgb.FromRgb24(cursor).ToHex() : "");
                SyncColorOverride("selection-background", SelectionColorPicker, SelectionColorResetButton,
                    () => ThemeParser.TryParseHexRgb(impl.GetRawFileValue("selection-background"), out var packed)
                        ? Rgb.FromRgb24(packed).ToHex()
                        : "");
                SyncColorOverride("accent-color", AccentColorPicker, AccentColorResetButton,
                    () => impl.AccentColor is uint accent ? Rgb.FromRgb24(accent).ToHex() : "");

                // The mode radios and the three boxes came last here, and that
                // is what made this page destructive in single-mode files: the
                // radios kept claiming single while the boxes behind them still
                // held the pair names, so the next commit wrote a
                // light:..,dark:.. pair over a theme the user had replaced.
                SeedThemePickers(impl);
            }
            finally
            {
                _loading = false;
            }
        });
    }

    // Seed one color row from the cached config: if the user has actually
    // set this key in their file, fill the picker and show the reset
    // button; otherwise leave both empty so the row reads as "unset".
    private void SyncColorOverride(string key, ColorPickerControl picker, Button resetButton, Func<string> resolvedValue)
    {
        if (_configService is not ConfigService cs) return;
        if (cs.IsConfiguredInFile(key))
        {
            picker.Color = resolvedValue();
            resetButton.Visibility = Visibility.Visible;
        }
        else
        {
            picker.Color = "";
            resetButton.Visibility = Visibility.Collapsed;
        }
    }
}
