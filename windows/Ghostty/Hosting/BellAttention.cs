using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Ghostty.Hosting;

/// <summary>
/// Requests user attention by flashing the window's taskbar button,
/// the Windows equivalent of the GTK urgency hint / macOS dock bounce
/// used by the bell <c>attention</c> feature.
/// </summary>
internal static class BellAttention
{
    public static void Flash(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // Attention for the window the user is already looking at is noise:
        // skip the flash when we are the foreground window.
        var handle = new HWND(hwnd);
        if (PInvoke.GetForegroundWindow() == handle) return;

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = handle,
            // Flash the taskbar button until the window is brought to the
            // foreground; mirrors the "until acknowledged" attention model.
            dwFlags = FLASHWINFO_FLAGS.FLASHW_TRAY | FLASHWINFO_FLAGS.FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0,
        };
        PInvoke.FlashWindowEx(in info);
    }
}
