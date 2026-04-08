using System;
using System.Runtime.InteropServices;
using Ghostty.Controls;
using Ghostty.Hosting;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace Ghostty;

public sealed partial class MainWindow : Window
{
    private readonly GhosttyHost _host;

    // Win32 interop for the window class background brush. WinUI 3 hosts
    // the XAML island inside a Win32 HWND whose WNDCLASS hbrBackground
    // defaults to white. During an interactive drag-resize, DWM paints
    // any uncovered window pixels with that brush BEFORE WinUI 3 gets a
    // chance to extend its XAML content into the new area, producing a
    // visible white flash at the leading edge of the drag. Replacing the
    // class brush with a dark solid brush makes the flash invisible
    // against any dark color scheme.
    private const int GCLP_HBRBACKGROUND = -10;

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    public MainWindow()
    {
        InitializeComponent();

        _host = new GhosttyHost(DispatcherQueue);
        Terminal.Host = _host;

        // Match the RootGrid background (#0C0C0C). Win32 COLORREF is 0x00BBGGRR.
        var hwnd = WindowNative.GetWindowHandle(this);
        var brush = CreateSolidBrush(0x000C0C0Cu);
        SetClassLongPtr(hwnd, GCLP_HBRBACKGROUND, brush);

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

        // Route title updates and close requests from the terminal's
        // runtime action callback to the window chrome. Both events
        // are raised on the UI thread by TerminalControl.
        Terminal.TitleChanged += (_, title) => Title = title;
        Terminal.CloseRequested += (sender, _) => RequestCloseLeaf((TerminalControl)sender!);
        Closed += (_, _) => _host.Dispose();
    }

    /// <summary>
    /// Handle a close request from a single terminal leaf.
    ///
    /// This is the close cascade entry point. The cascade runs:
    ///
    ///     leaf -> pane -> tab -> window
    ///
    /// In the multi-pane PR (next stacked) the leaf will be removed from
    /// its parent split, focus will move to the sibling subtree, and the
    /// window only closes when the last leaf goes away. With tabs (a
    /// later PR) the same logic runs at the tab level: closing the last
    /// leaf in a tab closes the tab; closing the last tab closes the
    /// window.
    ///
    /// PR 1 has exactly one leaf per window, so the cascade collapses to
    /// "close the window". The shape of this method is intentional - it
    /// is the single point that future code will extend, mirroring
    /// macOS's BaseTerminalController.closeSurface(_).
    /// </summary>
    private void RequestCloseLeaf(TerminalControl leaf)
    {
        // Today the only leaf is `Terminal`. When the surface tree exists,
        // this becomes: tree.remove(leaf); if (tree.IsEmpty) Close();
        if (leaf == Terminal) Close();
    }
}
