using Ghostty.Core.Config;
using Ghostty.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

internal sealed partial class TerminalPage : Page
{
    private readonly IConfigFileEditor _editor;
    private readonly SettingsConfigWriter _writer;
    private bool _loading = true;

    public TerminalPage(IConfigService configService, IConfigFileEditor editor)
    {
        _editor = editor;
        _writer = new SettingsConfigWriter(configService, StaticLoggers.SettingsConfigWriter);
        InitializeComponent();
        CursorStyleBox.ItemsSource = new[] { "block", "bar", "underline" };

        // Load current values so the UI reflects config on open, not
        // hard-coded defaults. Windows-only terminal properties are on
        // the concrete ConfigService; fall back to schema defaults if
        // the runtime type is different (e.g. in tests).
        if (configService is Ghostty.Services.ConfigService cs)
        {
            ScrollbackBox.Value = cs.ScrollbackLimit;
            CursorStyleBox.SelectedItem = cs.CursorStyle;
            CursorBlinkToggle.IsOn = cs.CursorBlink;
            MouseHideToggle.IsOn = cs.MouseHideWhileTyping;
        }

        _loading = false;
    }

    private void OnValueChanged(string key, string value)
    {
        if (_loading) return;
        _writer.Write(() => _editor.SetValue(key, value), key);
    }

    private void Scrollback_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        OnValueChanged("scrollback-limit", ((int)sender.Value).ToString());
    }

    private void CursorStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CursorStyleBox.SelectedItem is string style) OnValueChanged("cursor-style", style);
    }

    private void CursorBlink_Toggled(object sender, RoutedEventArgs e)
    {
        OnValueChanged("cursor-style-blink", CursorBlinkToggle.IsOn ? "true" : "false");
    }

    private void MouseHide_Toggled(object sender, RoutedEventArgs e)
    {
        OnValueChanged("mouse-hide-while-typing", MouseHideToggle.IsOn ? "true" : "false");
    }
}
