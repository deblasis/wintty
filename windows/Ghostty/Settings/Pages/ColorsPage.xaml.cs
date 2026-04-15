using System;
using Ghostty.Controls.Settings;
using Ghostty.Core.Config;
using Ghostty.Core.Settings;
using Ghostty.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

internal sealed partial class ColorsPage : Page
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;
    private readonly SearchableList _themeList;
    private readonly SearchableList _lightThemeList;
    private readonly SearchableList _darkThemeList;
    private bool _loading = true;

    public ColorsPage(IConfigService configService, IConfigFileEditor editor, IThemeProvider theme)
    {
        _configService = configService;
        _editor = editor;
        InitializeComponent();

        _themeList = new SearchableList(ThemeSearch, chosen => OnThemeChosen(chosen));
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

            var currentTheme = cs.CurrentTheme;
            if (cs.LightTheme is not null && cs.DarkTheme is not null)
            {
                // Pair mode.
                SingleModeRadio.IsChecked = false;
                PairModeRadio.IsChecked = true;
                SingleThemeCard.Visibility = Visibility.Collapsed;
                LightThemeCard.Visibility = Visibility.Visible;
                DarkThemeCard.Visibility = Visibility.Visible;
                LightThemeSearch.Text = cs.LightTheme;
                DarkThemeSearch.Text = cs.DarkTheme;
            }
            else
            {
                // Single mode (default).
                if (!string.IsNullOrEmpty(currentTheme))
                    ThemeSearch.Text = currentTheme;
            }
        }

        _loading = false;
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
        _configService.SuppressWatcher(true);
        try { _editor.SetValue(key, value); }
        finally { _configService.SuppressWatcher(false); }
        _configService.Reload();
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

    private void Foreground_Reset(object sender, RoutedEventArgs e)
        => ResetColorOverride("foreground", ForegroundPicker, ForegroundResetButton);

    private void Background_Reset(object sender, RoutedEventArgs e)
        => ResetColorOverride("background", BackgroundPicker, BackgroundResetButton);

    private void CursorColor_Reset(object sender, RoutedEventArgs e)
        => ResetColorOverride("cursor-color", CursorColorPicker, CursorColorResetButton);

    private void SelectionColor_Reset(object sender, RoutedEventArgs e)
        => ResetColorOverride("selection-background", SelectionColorPicker, SelectionColorResetButton);

    // Drop the override key from the config file, then clear the picker
    // and hide the reset button so the row reads as "no override set".
    // Suppressing the watcher keeps the file-change event from racing
    // the explicit Reload below; setting Color under the loading guard
    // blocks the picker's ColorChanged handler from firing a stray
    // OnValueChanged write back to disk.
    private void ResetColorOverride(string key, ColorPickerControl picker, Button resetButton)
    {
        if (_loading) return;
        _configService.SuppressWatcher(true);
        try { _editor.RemoveValue(key); }
        finally { _configService.SuppressWatcher(false); }
        _configService.Reload();

        _loading = true;
        try { picker.Color = ""; }
        finally { _loading = false; }
        resetButton.Visibility = Visibility.Collapsed;
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
