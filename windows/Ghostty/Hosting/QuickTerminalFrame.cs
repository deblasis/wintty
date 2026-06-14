using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;
using Ghostty.Core.Hosting;

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
/// The edge-direction math lives in <see cref="QuickTerminalFrameGeometry"/>
/// (pure, unit-tested); this file is only the Win32 plumbing. Win32 surface is
/// hand-written (matching <see cref="WindowsGlobalHotKey"/>) because the
/// SUBCLASSPROC is passed as a raw function pointer via
/// Marshal.GetFunctionPointerForDelegate, which the source-generated CsWin32
/// signatures do not model.
/// </summary>
internal sealed partial class QuickTerminalFrame : IDisposable
{
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_STYLECHANGING = 0x007C;
    private const uint WM_STYLECHANGED = 0x007D;

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
    private readonly Func<QuickTerminalPosition> _position;
    private readonly ILogger<QuickTerminalFrame>? _logger;

    // Held for the subclass's lifetime: the GC must not collect the delegate
    // while Windows holds its function pointer. GetFunctionPointerForDelegate
    // returns the same thunk for this instance, so the ctor and Dispose pass an
    // identical pointer to Set/RemoveWindowSubclass.
    private readonly SubclassProcDelegate _proc;

    // Resize-border depth in physical pixels. Computed once: the sizing border
    // is square on standard themes, so a single value covers both axes
    // (SM_CYSIZEFRAME + SM_CXPADDEDBORDER == the per-edge frame thickness). Read
    // at construction-time DPI; a few px off after a cross-monitor DPI change is
    // immaterial for an 8px grab strip, so it is deliberately not recomputed.
    private readonly int _grip;
    private bool _installed;

    // True while the caller is mutating the window's frame style (via
    // OverlappedPresenter.SetBorderAndTitleBar). The window proc swallows the
    // WM_STYLECHANGING/CHANGED notifications during that window so they never
    // reach the host WinUI window proc, which access-violates on a frame style
    // change while the window is in its early/unstable lifecycle. The style
    // change still takes effect; only the framework's reaction to it is dropped.
    private bool _suppressStyleChange;

    public QuickTerminalFrame(
        IntPtr hwnd,
        Func<QuickTerminalPosition> position,
        ILogger<QuickTerminalFrame>? logger)
    {
        ArgumentNullException.ThrowIfNull(position);
        _hwnd = hwnd;
        _position = position;
        _logger = logger;
        _proc = WndProc;
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
        else
        {
            // Degrade rather than throw: the window stays usable, it just keeps
            // its default sizing border (and the dark band). SetWindowSubclass
            // does not set a useful last error, so the warning stands alone.
            _logger?.LogQuakeFrameSubclassFailed();
        }
    }

    public void Dispose()
    {
        if (!_installed) return;
        // Best-effort: comctl32 also removes the subclass automatically on
        // WM_NCDESTROY, so if the HWND is already gone (teardown ordering) this
        // is a benign no-op. Either way no further message reaches _proc after
        // this, so the delegate is safe to collect once this instance is.
        RemoveWindowSubclass(_hwnd, Marshal.GetFunctionPointerForDelegate(_proc), SubclassId);
        _installed = false;
    }

    /// <summary>
    /// Arms style-change suppression for the duration of the returned scope.
    /// Wrap the caller's <c>SetBorderAndTitleBar</c> call in it: the borderless
    /// transition posts WM_STYLECHANGING/CHANGED into the host WinUI window proc,
    /// which access-violates while the window is still in its early lifecycle.
    /// The subclass eats those messages while armed, so the style change lands
    /// without the framework's crashy reaction.
    /// </summary>
    public IDisposable SuppressStyleChanges() => new StyleSuppressionScope(this);

    private sealed class StyleSuppressionScope : IDisposable
    {
        private readonly QuickTerminalFrame _owner;
        public StyleSuppressionScope(QuickTerminalFrame owner)
        {
            _owner = owner;
            _owner._suppressStyleChange = true;
        }
        public void Dispose() => _owner._suppressStyleChange = false;
    }

    private IntPtr WndProc(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        // While the caller is shaping the borderless frame, drop the style-change
        // notifications before they reach the host WinUI proc (which crashes on
        // them). Returning without DefSubclassProc swallows the message; the style
        // change itself is already applied by SetBorderAndTitleBar.
        if (_suppressStyleChange && (msg == WM_STYLECHANGING || msg == WM_STYLECHANGED))
            return IntPtr.Zero;

        var edge = QuickTerminalFrameGeometry.ResizableEdge(_position());

        // None == Center docking: no edge is flush to the monitor, so leave the
        // normal resizable frame on every edge (there is no band to hide).
        if (edge != QuickTerminalResizeEdge.None)
        {
            switch (msg)
            {
                // wParam == TRUE: lParam points at NCCALCSIZE_PARAMS whose first
                // RECT (rgrc[0], at offset 0) is the proposed window rect and
                // becomes the client rect on return. Pull the client out to every
                // edge except the resizable one, where a thin non-client strip is
                // kept so Windows still draws a sizing border there. Maximize is
                // disabled on this window, so no work-area clamp is needed.
                case WM_NCCALCSIZE when wParam != IntPtr.Zero:
                {
                    var rc = Marshal.PtrToStructure<RECT>(lParam);
                    switch (edge)
                    {
                        case QuickTerminalResizeEdge.Bottom: rc.bottom -= _grip; break;
                        case QuickTerminalResizeEdge.Top: rc.top += _grip; break;
                        case QuickTerminalResizeEdge.Left: rc.left += _grip; break;
                        case QuickTerminalResizeEdge.Right: rc.right -= _grip; break;
                    }
                    Marshal.StructureToPtr(rc, lParam, false);
                    return IntPtr.Zero;
                }

                case WM_NCHITTEST:
                    return (IntPtr)HitTest(lParam);
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Map the cursor position to a resize hit-code for the kept strip, or
    /// HTCLIENT everywhere else. The strip is the only non-client area left, so
    /// it is the only place the cursor reaches this proc.
    /// </summary>
    private int HitTest(IntPtr lParam)
    {
        if (!GetWindowRect(_hwnd, out var r))
            return HTCLIENT;

        // lParam packs signed screen coords: x in the low word, y in the high.
        long lp = lParam.ToInt64();
        int x = unchecked((short)(lp & 0xFFFF));
        int y = unchecked((short)((lp >> 16) & 0xFFFF));

        return QuickTerminalFrameGeometry.HitTest(
            _position(), r.left, r.top, r.right, r.bottom, x, y, _grip) switch
        {
            QuickTerminalResizeEdge.Bottom => HTBOTTOM,
            QuickTerminalResizeEdge.Top => HTTOP,
            QuickTerminalResizeEdge.Left => HTLEFT,
            QuickTerminalResizeEdge.Right => HTRIGHT,
            _ => HTCLIENT,
        };
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

internal static partial class QuickTerminalFrameLogExtensions
{
    // Warning, not Error: a failed subclass just means the quake window keeps
    // its default sizing border. The window still works.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Hosting.QuakeFrameSubclassFailed,
                   Level = LogLevel.Warning,
                   Message = "[QuickTerminalFrame] SetWindowSubclass failed; quake window keeps its default sizing border")]
    internal static partial void LogQuakeFrameSubclassFailed(this ILogger<QuickTerminalFrame> logger);
}
