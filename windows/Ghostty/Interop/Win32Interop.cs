using System;
using System.Runtime.InteropServices;

namespace Ghostty.Interop;

/// <summary>
/// Win32 P/Invoke declarations for window transparency control.
/// </summary>
internal static partial class Win32Interop
{
    [LibraryImport("gdi32.dll")]
    public static partial IntPtr GetStockObject(int fnObject);

    // NULL_BRUSH (HOLLOW_BRUSH) -- transparent class brush so
    // composition alpha shows through to the desktop.
    public const int NULL_BRUSH = 5;

    // --- DWM APIs for crystal (zero-blur) transparency ---

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_BLURBEHIND
    {
        public uint DwFlags;
        public int FEnable;
        public IntPtr HRgnBlur;
        public int FTransitionOnMaximized;

        public const uint DWM_BB_ENABLE = 0x01;
        public const uint DWM_BB_BLURREGION = 0x02;
    }

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmExtendFrameIntoClientArea(
        IntPtr hwnd, ref MARGINS margins);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmEnableBlurBehindWindow(
        IntPtr hwnd, ref DWM_BLURBEHIND blurBehind);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateRectRgn(
        int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr hObject);
}
