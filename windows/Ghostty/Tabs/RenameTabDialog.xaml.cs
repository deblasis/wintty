using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Tabs;

internal sealed partial class RenameTabDialog : ContentDialog
{
    public string? Result { get; private set; }

    public RenameTabDialog(string? initial)
    {
        InitializeComponent();
        TitleBox.Text = initial ?? string.Empty;
        PrimaryButtonClick += (_, _) => Result = TitleBox.Text;
    }
}
