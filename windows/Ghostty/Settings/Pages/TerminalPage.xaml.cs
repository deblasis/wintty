using Ghostty.Core.Config;
using Ghostty.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

internal sealed partial class TerminalPage : Page
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;
    private readonly SettingsConfigWriter _writer;
    private bool _loading = true;

    public TerminalPage(IConfigService configService, IConfigFileEditor editor)
    {
        _configService = configService;
        _editor = editor;
        _writer = new SettingsConfigWriter(configService, StaticLoggers.SettingsConfigWriter);
        InitializeComponent();
        CursorStyleBox.ItemsSource = new[] { "block", "bar", "underline" };
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
    /// SettingsWindow caches page instances, so what <see cref="LoadValues"/>
    /// seeded in the constructor describes the config as of whenever the page
    /// was built. A control left behind writes the stale value the next time
    /// the user nudges it, so the edit is silently undone -- and the raw
    /// editor in this same dialog is enough to make that happen without
    /// leaving the settings window.
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
    /// Put every control on this page in step with the config.
    ///
    /// Windows-only terminal properties are on the concrete ConfigService, so
    /// a different runtime type (a test fake) leaves the schema defaults in
    /// the markup, which is where they already were.
    /// </summary>
    private void LoadValues()
    {
        if (_configService is not Ghostty.Services.ConfigService cs) return;

        ScrollbackBox.Value = cs.ScrollbackLimit;
        CursorStyleBox.SelectedItem = cs.CursorStyle;
        CursorBlinkToggle.IsOn = cs.CursorBlink;
        MouseHideToggle.IsOn = cs.MouseHideWhileTyping;
    }

    private void OnValueChanged(string key, string value)
    {
        if (_loading) return;
        _writer.Write(() => _editor.SetValue(key, value), key);
    }

    private void Scrollback_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        OnValueChanged("scrollback-limit", ((int)sender.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));
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