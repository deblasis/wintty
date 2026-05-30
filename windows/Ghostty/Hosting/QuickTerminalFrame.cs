using System;
using System.Runtime.InteropServices;

namespace Ghostty.Hosting;

/// <summary>
/// Tames the borderless quake window's non-client frame by subclassing its
/// window procedure. A resizable (WS_THICKFRAME) borderless window still gets
/// an ~8px sizing border on every edge, which Windows paints as a dark band at
/// the docked edge and exposes as a resize grip there -- neither wanted on an
/// edge that sits flush against the monitor.
///
/// Two messages are intercepted:
///  - WM_NCCALCSIZE: the client area is pulled out to every edge EXCEPT the one
///    opposite the dock, where a thin non-client strip is kept. The frame (and
///    its dark band) is gone on the docked edge and the two perpendicular edges;
///    the kept strip is the only place Windows still draws a sizing border.
///  - WM_NCHITTEST: that one strip reports a resize grip; everything else is
///    client. So only the edge opposite the dock is drag-resizable.
///
/// Keeping the strip non-client matters: WinUI hosts the XAML content in a
/// child HWND that covers the whole client area, so an edge made fully client
/// can no longer be hit-tested for resize. The strip stays uncovered, which is
/// why native OS resize (and its cursor) still works there.
///
/// Win32 surface is hand-written (matching <see cref="WindowsGlobalHotKey"/>)
/// because the SUBCLASSPROC is passed as a raw function pointer via
/// Marshal.GetFunctionPointerForDelegate, which the source-generated CsWin32
/// signatures do not model.
/// </summary>
internal sealed partial class QuickTerminalFrame : IDisposable
{
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCHITTEST = 0x0084;

    // WM_NCHITTEST return codes.
    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTBOTTOM = 15;

    // GetSystemMetrics indices for the sizing border thickness.
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    // SetWindowPos flags to force a one-time WM_NCCALCSIZE recompute so the
    // frame is dropped immediately rather than on the next natural resize.
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    // Arbitrary non-zero id distinguishing this subclass from any other on
    // the same window.
    private static readonly UIntPtr SubclassId = (UIntPtr)0x5157; // 'QW'

    private readonly IntPtr _hwnd;
    private readonly Func<Ghostty.Core.Hosting.QuickTerminalPosition> _position;

    // Held for the subclass's lifetime: the GC must not collect the delegate
    // while Windows holds its function pointer.
    private readonly SubclassProcDelegate _proc;
    private readonly int _grip;
    private bool _installed;

    public QuickTerminalFrame(
        IntPtr hwnd,
        Func<Ghostty.Core.Hosting.QuickTerminalPosition> position)
    {
        ArgumentNullException.ThrowIfNull(position);
        _hwnd = hwnd;
        _position = position;
        _proc = WndProc;
        // Resize-grip depth = the sizing border Windows would have drawn, so the
        // grab zone matches what the user expects from a normal window edge.
        _grip = Math.Max(1, GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER));

        _installed = SetWindowSubclass(
            _hwnd, Marshal.GetFunctionPointerForDelegate(_proc), SubclassId, UIntPtr.Zero);
        if (_installed)
        {
            // Force the frame to recompute now so WM_NCCALCSIZE drops the band
            // before the first show instead of waiting for a resize.
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    public void Dispose()
    {
        if (_installed)
        {
            RemoveWindowSubclass(_hwnd, Marshal.GetFunctionPointerForDelegate(_proc), SubclassId);
            _installed = false;
        }
    }

    private IntPtr WndProc(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        var pos = _position();

        // Center docking has no edge flush against the monitor, so there is no
        // band to hide: keep the normal resizable frame on every edge.
        if (pos != Ghostty.Core.Hosting.QuickTerminalPosition.Center)
        {
            switch (msg)
            {
                // wParam == TRUE: lParam points at NCCALCSIZE_PARAMS whose first
                // RECT (rgrc[0], at offset 0) is the proposed window rect and
                // becomes the client rect on return. Pull the client out to every
                // edge EXCEPT the one opposite the dock, where a thin non-client
                // strip is kept. That strip is the only thing Windows can draw a
                // resize border on (and hit-test for the drag); removing the
                // frame on the other three edges drops the dark sizing band,
                // including the prominent one at the docked edge.
                case WM_NCCALCSIZE when wParam != IntPtr.Zero:
                {
                    var rc = Marshal.PtrToStructure<RECT>(lParam);
                    switch (pos)
                    {
                        case Ghostty.Core.Hosting.QuickTerminalPosition.Top: rc.bottom -= _grip; break;
                        case Ghostty.Core.Hosting.QuickTerminalPosition.Bottom: rc.top += _grip; break;
                        case Ghostty.Core.Hosting.QuickTerminalPosition.Left: rc.right -= _grip; break;
                        case Ghostty.Core.Hosting.QuickTerminalPosition.Right: rc.left += _grip; break;
                    }
                    Marshal.StructureToPtr(rc, lParam, false);
                    return IntPtr.Zero;
                }

                case WM_NCHITTEST:
                    return (IntPtr)HitTest(lParam, pos);
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Report a resize grip on the edge opposite the docked edge -- the only
    /// edge left non-client by WM_NCCALCSIZE, so it is the only one the cursor
    /// can reach here (XAML's content HWND covers the client edges). Anything
    /// else is client.
    /// </summary>
    private int HitTest(IntPtr lParam, Ghostty.Core.Hosting.QuickTerminalPosition pos)
    {
        if (!GetWindowRect(_hwnd, out var r))
            return HTCLIENT;

        // lParam packs signed screen coords: x in the low word, y in the high.
        long lp = lParam.ToInt64();
        int x = unchecked((short)(lp & 0xFFFF));
        int y = unchecked((short)((lp >> 16) & 0xFFFF));

        return pos switch
        {
            Ghostty.Core.Hosting.QuickTerminalPosition.Top =>
                y >= r.bottom - _grip ? HTBOTTOM : HTCLIENT,
            Ghostty.Core.Hosting.QuickTerminalPosition.Bottom =>
                y <= r.top + _grip ? HTTOP : HTCLIENT,
            Ghostty.Core.Hosting.QuickTerminalPosition.Left =>
                x >= r.right - _grip ? HTRIGHT : HTCLIENT,
            Ghostty.Core.Hosting.QuickTerminalPosition.Right =>
                x <= r.left + _grip ? HTLEFT : HTCLIENT,
            _ => HTCLIENT,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProcDelegate(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [LibraryImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(
        IntPtr hWnd, IntPtr pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [LibraryImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(
        IntPtr hWnd, IntPtr pfnSubclass, UIntPtr uIdSubclass);

    [LibraryImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
