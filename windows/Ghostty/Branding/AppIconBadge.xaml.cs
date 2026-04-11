using Ghostty.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Ghostty.Branding;

/// <summary>
/// Notepad-style app icon that sits at the left edge of the tab strip.
/// Click opens the Windows system menu over the badge. Instantiated
/// once in TabHost (horizontal mode) and once in VerticalTabHost
/// (vertical mode); both live in subtrees that are only ever visible
/// one at a time.
/// </summary>
public sealed partial class AppIconBadge : UserControl
{
    // Instance property returning a BitmapImage so x:Bind's type check
    // matches ImageIcon.Source (ImageSource). Mode=OneTime means the
    // XAML compiler emits a direct call at InitializeComponent; AOT-safe.
    public BitmapImage IconSource { get; } = new BitmapImage(AppIconSource.Current);

    public AppIconBadge()
    {
        InitializeComponent();
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        var window = WindowHelper.GetWindow(this);
        if (window is null) return;
        SystemMenuInterop.ShowAt(window, ClickTarget);
    }
}
