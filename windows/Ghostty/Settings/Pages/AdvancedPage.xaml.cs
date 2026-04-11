using Ghostty.Core.Config;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

internal sealed partial class AdvancedPage : Page
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;

    public AdvancedPage(IConfigService configService, IConfigFileEditor editor)
    {
        _configService = configService;
        _editor = editor;
        InitializeComponent();
    }
}
