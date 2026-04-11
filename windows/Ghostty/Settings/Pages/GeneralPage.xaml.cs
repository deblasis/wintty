using Ghostty.Core.Config;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

internal sealed partial class GeneralPage : Page
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;
    private bool _loading = true;

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
        VerticalTabsToggle.IsOn = Ghostty.Settings.UiSettings.Load().VerticalTabs;
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
        var settings = Ghostty.Settings.UiSettings.Load();
        settings.VerticalTabs = VerticalTabsToggle.IsOn;
        settings.Save();
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
