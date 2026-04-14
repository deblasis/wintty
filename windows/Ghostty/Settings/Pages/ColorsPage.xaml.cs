using Ghostty.Core.Config;
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

        // Determine initial mode from current config.
        if (configService is ConfigService cs)
        {
            var currentTheme = cs.CurrentTheme;
            if (cs.LightTheme is not null && cs.DarkTheme is not null)
            {
                // Pair mode.
                SingleModeRadio.IsChecked = false;
                PairModeRadio.IsChecked = true;
                ThemeSearch.Visibility = Visibility.Collapsed;
                PairThemePanel.Visibility = Visibility.Visible;
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

        if (PairModeRadio.IsChecked == true)
        {
            // Switching to pair mode: seed both boxes from the current
            // single theme so the user sees their selection carried over.
            var current = ThemeSearch.Text.Trim();
            if (!string.IsNullOrEmpty(current))
            {
                LightThemeSearch.Text = current;
                DarkThemeSearch.Text = current;
            }

            ThemeSearch.Visibility = Visibility.Collapsed;
            PairThemePanel.Visibility = Visibility.Visible;
        }
        else
        {
            // Switching to single mode: pick the dark theme as default
            // (most users run dark mode), falling back to light.
            var fallback = DarkThemeSearch.Text.Trim();
            if (string.IsNullOrEmpty(fallback))
                fallback = LightThemeSearch.Text.Trim();

            ThemeSearch.Visibility = Visibility.Visible;
            PairThemePanel.Visibility = Visibility.Collapsed;

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
        _editor.SetValue(key, value);
        _configService.SuppressWatcher(false);
        _configService.Reload();
    }

    private void Foreground_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) OnValueChanged("foreground", tb.Text);
    }

    private void Background_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) OnValueChanged("background", tb.Text);
    }

    private void CursorColor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) OnValueChanged("cursor-color", tb.Text);
    }

    private void SelectionColor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) OnValueChanged("selection-background", tb.Text);
    }
}
