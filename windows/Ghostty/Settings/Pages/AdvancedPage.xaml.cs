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
    }

    private void LoadValues()
    {
        HighContrastToggle.IsOn = _configService.WindowsHighContrast;
        SelectComboByTag(LogLevelCombo, _configService.LogLevel);
        LogFilterBox.Text = _configService.LogFilter ?? string.Empty;

        if (_cs is null) return;

        SingleInstanceToggle.IsOn = WindowsOnlyKeyParsers.ParseBool(
            _cs.GetRawFileValue("windows-single-instance"),
            defaultValue: false);

        var quake = _cs.GetRawFileValue("quick-terminal-key");
        QuakeKeyBox.Text = string.IsNullOrWhiteSpace(quake) ? string.Empty : quake;
    }

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
        Ghostty.App.ConfigWriteScheduler?.Schedule("quick-terminal-key", raw);
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
        Ghostty.App.ConfigWriteScheduler?.Schedule(
            "log-filter",
            LogFilterBox.Text?.Trim() ?? string.Empty);
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
