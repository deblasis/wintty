using System;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Ghostty.Branding;

/// <summary>
/// Opens the Windows system menu (Restore / Move / Size / Minimize /
/// Maximize / Close) over a WinUI 3 custom title bar element. Matches
/// Notepad's caption-icon behavior on the WinUI 3 side where the non-
/// client area is extended and the OS does not draw its own caption icon.
///
/// Two entry points share the same tracking core: <see cref="ShowAt"/>
/// anchors at a title-bar element (the app-icon badge click) and
/// <see cref="ShowForActiveWindow"/> anchors at the window's top-left
/// frame corner (the Alt+Space keyboard chord).
///
/// Migrated from Interop/SystemMenuInterop.cs to use CsWin32-generated
/// user32 entry points. BOOL is Windows.Win32.Foundation.BOOL (4 bytes,
/// implicit bool conversion). CsWin32 picks System.Drawing.Point as the
/// friendly POINT shape (uppercase X/Y), so this file uses Point.X/Point.Y
/// rather than the lowercase x/y of the raw winuser.h struct.
/// </summary>
internal static class SystemMenuPopup
{
    /// <summary>
    /// Show the menu anchored at the bottom-left of <paramref name="anchor"/>
    /// in screen coordinates (the title-bar app-icon badge click path).
    /// </summary>
    public static void ShowAt(Window window, FrameworkElement anchor)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(anchor);

        // XamlRoot can be null if the anchor hasn't been attached to a
        // visual tree yet (reparented, pre-Loaded, or a programmatic
        // click fired during startup). Bail out instead of NREing.
        if (anchor.XamlRoot is null) return;

        var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(window));

        // Anchor at the badge's bottom-left corner in screen coordinates.
        // DIP -> physical pixels via the anchor's XamlRoot RasterizationScale.
        var transform = anchor.TransformToVisual(null);
        var bottomLeftDip = transform.TransformPoint(new Point(0, anchor.ActualHeight));
        var scale = anchor.XamlRoot.RasterizationScale;

        var clientPoint = new System.Drawing.Point(
            (int)(bottomLeftDip.X * scale),
            (int)(bottomLeftDip.Y * scale));
        PInvoke.ClientToScreen(hwnd, ref clientPoint);

        Track(hwnd, clientPoint.X, clientPoint.Y);
    }

    /// <summary>
    /// Show the menu anchored at the top-left of <paramref name="handle"/>'s
    /// frame (the Alt+Space keyboard chord path). The hook captures the
    /// target window when the chord fires and passes it here, so the menu
    /// always belongs to the window that had focus at the keypress even if
    /// focus moves before this runs. <c>GetWindowRect</c> returns physical
    /// screen pixels, which is what <c>TrackPopupMenu</c> wants, so the
    /// menu lands where the native Alt+Space menu would.
    /// </summary>
    public static void ShowForWindow(IntPtr handle)
    {
        var hwnd = new HWND(handle);
        if (hwnd == HWND.Null) return;
        if (!PInvoke.GetWindowRect(hwnd, out var rect)) return;
        Track(hwnd, rect.left, rect.top);
    }

    /// <summary>
    /// Resolve the window's system menu, pop it at the given physical
    /// screen coordinates, and dispatch the chosen command back to the
    /// window.
    /// </summary>
    private static void Track(HWND hwnd, int screenX, int screenY)
    {
        var hmenu = PInvoke.GetSystemMenu(hwnd, bRevert: false);
        if (hmenu == HMENU.Null) return;

        // TrackPopupMenu documentation requires the owning window to be
        // the foreground window, or the menu may dismiss itself
        // unexpectedly on the next click. It is also what lets the menu
        // take keyboard focus (arrows / Enter / Esc).
        PInvoke.SetForegroundWindow(hwnd);

        // TPM_RETURNCMD makes TrackPopupMenu return the selected command
        // id (int) instead of dispatching WM_COMMAND itself. The BOOL
        // return type in CsWin32 here holds that integer; 0 = cancel.
        var cmd = PInvoke.TrackPopupMenu(
            hmenu,
            TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD,
            screenX,
            screenY,
            hwnd,
            prcRect: null);

        // Dispatch synchronously: SC_MOVE / SC_SIZE enter modal
        // mouse-tracking loops and rely on stable activation state.
        if (cmd.Value != 0)
        {
            PInvoke.SendMessage(
                hwnd,
                PInvoke.WM_SYSCOMMAND,
                (WPARAM)(uint)cmd.Value,
                lParam: default);
        }
    }
}
