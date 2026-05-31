using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ghostty.Hosting;

/// <summary>
/// Swallows <c>WM_SYSCHAR</c> on a WinUI 3 window's input-site child HWND
/// so Win32's <c>DefWindowProc</c> stops ringing the "no menu item matched"
/// beep on every Alt chord.
///
/// Why a child HWND: in WinUI 3 the top-level
/// <c>WinUIDesktopWin32WindowClass</c> does NOT receive keyboard input.
/// Input is routed to a nested <c>InputSiteWindowClass</c> HWND (a child of
/// <c>Microsoft.UI.Content.DesktopChildSiteBridge</c>), so the subclass has
/// to go there or it never sees <c>WM_SYSCHAR</c>.
///
/// Why this is needed: the Windows default keybinds put splits on
/// <c>Alt+Shift+=</c> / <c>Alt+Shift+-</c> (Windows Terminal parity). Those
/// are system keys, so Windows emits a <c>WM_SYSCHAR</c> for the
/// <c>=</c>/<c>-</c>. libghostty already consumed the chord on key-down, and
/// marking the WinUI <c>KeyDown</c> handled does not stop the trailing
/// <c>WM_SYSCHAR</c> from reaching <c>DefWindowProc</c> and beeping.
///
/// Why this is safe for a terminal: Alt+&lt;char&gt; is a meta sequence
/// handled by the key encoder on key-down, never legitimate
/// <c>WM_SYSCHAR</c> text. AltGr characters (the international
/// <c>@ # [ ] { }</c> on Italian/German layouts) arrive as ordinary
/// <c>WM_CHAR</c>, not <c>WM_SYSCHAR</c>, so they pass through untouched.
/// </summary>
internal sealed partial class SysCharBeepSuppressor : IDisposable
{
    private const uint WM_SYSCHAR = 0x0106;
    private const int GWLP_WNDPROC = -4;
    private const string InputSiteClass = "InputSiteWindowClass";

    // One shared proc for every subclassed HWND; per-HWND original procs
    // are looked up in _oldProcs. Held in a field (plus its function
    // pointer) so the GC cannot collect the delegate while Win32 holds the
    // pointer as the window proc.
    private readonly WndProcDelegate _proc;
    private readonly IntPtr _procPtr;
    private readonly Dictionary<IntPtr, IntPtr> _oldProcs = new();

    public SysCharBeepSuppressor()
    {
        _proc = WndProc;
        _procPtr = Marshal.GetFunctionPointerForDelegate(_proc);
    }

    /// <summary>
    /// Subclass every <c>InputSiteWindowClass</c> descendant of the given
    /// top-level window that is not already subclassed. Idempotent and
    /// cheap, so it is safe to call on each <c>Activated</c> until the input
    /// site has been created. Returns the number of HWNDs subclassed so far.
    /// </summary>
    public int Install(IntPtr topLevel)
    {
        if (topLevel == IntPtr.Zero) return _oldProcs.Count;

        EnumChildProc cb = (hWnd, _) =>
        {
            if (!_oldProcs.ContainsKey(hWnd) && GetClassName(hWnd) == InputSiteClass)
            {
                var old = SetWindowLongPtrW(hWnd, GWLP_WNDPROC, _procPtr);
                if (old != IntPtr.Zero) _oldProcs[hWnd] = old;
            }
            return true;
        };

        EnumChildWindows(topLevel, Marshal.GetFunctionPointerForDelegate(cb), IntPtr.Zero);
        GC.KeepAlive(cb);
        return _oldProcs.Count;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Swallow every WM_SYSCHAR (do not chain) so DefWindowProc never
        // rings the "no menu item matched" beep for an Alt chord.
        if (msg == WM_SYSCHAR)
            return IntPtr.Zero;

        return _oldProcs.TryGetValue(hWnd, out var old) && old != IntPtr.Zero
            ? CallWindowProcW(old, hWnd, msg, wParam, lParam)
            : DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        foreach (var kv in _oldProcs)
            SetWindowLongPtrW(kv.Key, GWLP_WNDPROC, kv.Value);
        _oldProcs.Clear();
    }

    private static unsafe string GetClassName(IntPtr hWnd)
    {
        Span<char> buf = stackalloc char[64];
        int len;
        fixed (char* p = buf)
        {
            len = GetClassNameW(hWnd, p, buf.Length);
        }
        return len > 0 ? new string(buf[..len]) : string.Empty;
    }

    // ----- hand-written P/Invoke ----------------------------------------
    // Hand-written (not CsWin32) so the WndProc-as-IntPtr subclassing shape
    // stays obvious and local, matching WindowsGlobalHotKey. The callback is
    // passed as a function pointer (not a marshalled delegate parameter) for
    // the same reason that file does it.

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CallWindowProcW(
        IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "EnumChildWindows")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumChildWindows(IntPtr hWndParent, IntPtr lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);
}
