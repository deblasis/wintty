using System;
using Ghostty.Core.Config;
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
    }

    private void AutoReloadToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _configService.SuppressWatcher(true);
        _editor.SetValue("auto-reload-config", AutoReloadToggle.IsOn ? "true" : "false");
        _configService.SuppressWatcher(false);
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
            System.Diagnostics.Debug.WriteLine($"Failed to open config file: {ex.Message}");
        }
    }
}
