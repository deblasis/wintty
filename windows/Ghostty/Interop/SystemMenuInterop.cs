using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Ghostty.Interop;

/// <summary>
/// Opens the Windows system menu (Restore / Move / Size / Minimize /
/// Maximize / Close) over a WinUI 3 custom title bar element. Matches
/// Notepad's caption-icon behavior on the WinUI 3 side where the non-
/// client area is extended and the OS does not draw its own caption icon.
///
/// Hand-written LibraryImport follows the existing project convention
/// in Interop/NativeMethods.cs and Interop/ShellInterop.cs. BOOL is
/// represented as int (0 = FALSE, non-zero = TRUE) because
/// [assembly: DisableRuntimeMarshalling] forbids [MarshalAs] on
/// LibraryImport signatures.
///
/// Every [LibraryImport] is marked with
/// [DefaultDllImportSearchPaths(System32)] so the Win32 DLLs resolve
/// from %WINDIR%\System32 and not from the app directory or a hijacked
/// PATH entry.
/// </summary>
internal static partial class SystemMenuInterop
{
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint WM_SYSCOMMAND = 0x0112;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // hWnd, BOOL bRevert (0 = retrieve, non-zero = reset default)
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr GetSystemMenu(IntPtr hWnd, int bRevert);

    // Returns command id (int), 0 if cancelled OR on error (disambiguate
    // with Marshal.GetLastPInvokeError when needed).
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int TrackPopupMenu(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        IntPtr hWnd,
        IntPtr prcRect);

    // Returns BOOL (int). Non-zero on success.
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    // WM_SYSCOMMAND is dispatched synchronously via SendMessage so the
    // caller's window-activation state is stable when SC_MOVE / SC_SIZE
    // enter their modal mouse-tracking loops. PostMessage would queue
    // the command behind any pending input and can race the activation
    // state for those two specific commands.
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr SendMessage(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam);

    // TrackPopupMenu documentation requires the owning window to be the
    // foreground window, or the menu may dismiss itself unexpectedly on
    // the next click. Click-triggered invocations already satisfy this
    // because the click activates our window, but calling it explicitly
    // covers keyboard-triggered paths (Shift+F10 over the badge) that
    // may be added later without having to revisit this file.
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetForegroundWindow(IntPtr hWnd);

    public static void ShowAt(Window window, FrameworkElement anchor)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(anchor);

        // XamlRoot can be null if the anchor hasn't been attached to a
        // visual tree yet (reparented, pre-Loaded, or a programmatic
        // click fired during startup). Bail out instead of NREing.
        if (anchor.XamlRoot is null) return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var hmenu = GetSystemMenu(hwnd, 0);
        if (hmenu == IntPtr.Zero) return;

        SetForegroundWindow(hwnd);

        // Anchor at the badge's bottom-left corner in screen coordinates.
        // DIP -> physical pixels via the anchor's XamlRoot RasterizationScale.
        var transform = anchor.TransformToVisual(null);
        var bottomLeftDip = transform.TransformPoint(new Point(0, anchor.ActualHeight));
        var scale = anchor.XamlRoot.RasterizationScale;

        var clientPoint = new POINT
        {
            X = (int)(bottomLeftDip.X * scale),
            Y = (int)(bottomLeftDip.Y * scale),
        };
        ClientToScreen(hwnd, ref clientPoint);

        var cmd = TrackPopupMenu(
            hmenu,
            TPM_RETURNCMD,
            clientPoint.X,
            clientPoint.Y,
            0,
            hwnd,
            IntPtr.Zero);

        if (cmd != 0)
        {
            SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero);
        }
    }
}
