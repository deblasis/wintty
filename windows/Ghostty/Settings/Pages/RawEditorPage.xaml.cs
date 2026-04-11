using Ghostty.Core.Config;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

internal sealed partial class RawEditorPage : Page
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;

    public RawEditorPage(IConfigService configService, IConfigFileEditor editor)
    {
        _configService = configService;
        _editor = editor;
        InitializeComponent();
        LoadContent();
    }

    private void LoadContent()
    {
        Editor.Text = _editor.ReadAll();
        StatusText.Text = $"Loaded from {_configService.ConfigFilePath}";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _configService.SuppressWatcher(true);
        _editor.WriteRaw(Editor.Text);
        _configService.SuppressWatcher(false);

        var success = _configService.Reload();
        StatusText.Text = success
            ? $"Saved and reloaded ({_configService.DiagnosticsCount} diagnostics)"
            : "Reload failed -- check diagnostics";
    }

    private void RevertButton_Click(object sender, RoutedEventArgs e)
    {
        LoadContent();
    }
}
