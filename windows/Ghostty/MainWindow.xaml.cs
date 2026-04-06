using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Ghostty;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Mica is only available on Windows 11 22H1+ with a supported GPU.
        // Our TargetPlatformMinVersion is still 19041 (Windows 10), so a
        // bare `new MicaBackdrop()` would silently fail on older systems
        // and leave the window with a transparent black backdrop. Probe
        // MicaController.IsSupported() and skip the backdrop on unsupported
        // hosts; XAML's default Window background takes over.
        //
        // Custom title bar with ExtendsContentIntoTitleBar comes in a
        // follow-up PR so we can focus on the terminal plumbing here.
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();
    }
}
