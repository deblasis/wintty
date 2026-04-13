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
    private bool _loading = true;

    public ColorsPage(IConfigService configService, IConfigFileEditor editor, IThemeProvider theme)
    {
        _configService = configService;
        _editor = editor;
        InitializeComponent();
        _themeList = new SearchableList(ThemeSearch, chosen => OnValueChanged("theme", chosen));
        _themeList.SetItems(theme.AvailableThemes);

        // Show the current theme in the search box.
        if (configService is ConfigService cs && !string.IsNullOrEmpty(cs.CurrentTheme))
            ThemeSearch.Text = cs.CurrentTheme;

        _loading = false;
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
