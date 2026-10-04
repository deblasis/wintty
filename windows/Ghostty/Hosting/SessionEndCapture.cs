using System;
using System.Runtime.InteropServices;

namespace Ghostty.Hosting;

/// <summary>
/// Subclasses a main window's top-level WndProc so a session end still
/// writes the session file as a clean shutdown.
///
/// Why this has to exist: Windows ends a session (sign-out, reboot, a
/// Windows Update restart) by sending WM_QUERYENDSESSION and then
/// WM_ENDSESSION to every top-level window and then killing the process.
/// It does NOT send WM_CLOSE, so <c>Window.Closed</c> never fires, so the
/// clean-shutdown write that <c>window-save-state=default</c> keys on never
/// ran. The running file was therefore always unclean and the next launch
/// opened one fresh window -- which is the whole of the reported
/// "no session restore after a reboot".
///
/// Why the top-level HWND and not the input-site child the beep suppressor
/// subclasses: those two session messages are broadcast to top-level
/// windows only, so a subclass below the top level would never see them.
/// Unlike the beep suppressor this needs no retry on Activated beyond the
/// one below -- the session messages cannot arrive before the HWND exists,
/// and the top-level HWND does not move afterwards.
///
/// The split between <see cref="Classify"/> and <see cref="Dispatch"/> is
/// deliberate: the policy (which message means what) is a pure function of
/// (msg, wParam) and is testable without an HWND, while the dispatch is the
/// one line that has a side effect. Nothing here signs anything out -- a
/// test hands <see cref="Dispatch"/> the message and a fake capture.
///
/// The capture is synchronous on purpose. Windows gives a process a short,
/// bounded window at session end and then ends it; there is nothing to await.
/// </summary>
internal sealed partial class SessionEndCapture : IDisposable
{
    internal const uint WM_QUERYENDSESSION = 0x0011;
    internal const uint WM_ENDSESSION = 0x0016;

    private const int GWLP_WNDPROC = -4;

    /// <summary>What the window procedure is to do about one message.</summary>
    internal enum SessionEndAction
    {
        /// <summary>Not ours: chain to the previous proc untouched.</summary>
        None,

        /// <summary>
        /// Answer the session-end question and stop: TRUE, nothing to veto.
        /// </summary>
        Allow,

        /// <summary>The session really is ending: capture, then chain.</summary>
        Capture,
    }

    // Delegate plus its function pointer are held in fields so the GC cannot
    // collect the proc while Win32 holds the pointer -- the shape
    // SysCharBeepSuppressor uses for the same reason.
    private readonly Action _capture;
    private readonly WndProcDelegate _proc;
    private readonly IntPtr _procPtr;
    private IntPtr _hwnd;
    private IntPtr _oldProc;

    /// <param name="capture">
    /// Runs synchronously on the UI thread when a session is really ending.
    /// Must not throw past this class; a failure here is swallowed and the
    /// message is chained, because a session end that hangs or crashes costs
    /// the whole logon.
    /// </param>
    public SessionEndCapture(Action capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _capture = capture;
        _proc = WndProc;
        _procPtr = Marshal.GetFunctionPointerForDelegate(_proc);
    }

    /// <summary>
    /// Subclass <paramref name="topLevel"/>'s window procedure. Idempotent:
    /// a second call is a no-op rather than a second chain over the first.
    /// False when there is no window to subclass yet, so the caller can
    /// retry on the next activation.
    /// </summary>
    public bool Install(IntPtr topLevel)
    {
        if (topLevel == IntPtr.Zero) return false;
        if (_hwnd != IntPtr.Zero) return true;
        var old = SetWindowLongPtrW(topLevel, GWLP_WNDPROC, _procPtr);
        if (old == IntPtr.Zero) return false;
        _hwnd = topLevel;
        _oldProc = old;
        return true;
    }

    /// <summary>
    /// The whole policy as a pure function of the message and its wParam.
    ///
    /// WM_QUERYENDSESSION is answered, not acted on: the app has nothing to
    /// veto, and there is nothing worth saving yet -- Windows may still call
    /// the whole thing off (WM_ENDSESSION with wParam=FALSE), and a window
    /// that captured at the query would save a session for an end that never
    /// happened. So it is consumed (TRUE) and left alone.
    ///
    /// WM_ENDSESSION with wParam=TRUE is the commit: capture, then chain.
    /// wParam=FALSE is the cancellation and does nothing.
    /// </summary>
    internal static SessionEndAction Classify(uint msg, IntPtr wParam) => msg switch
    {
        WM_QUERYENDSESSION => SessionEndAction.Allow,
        WM_ENDSESSION => wParam != IntPtr.Zero
            ? SessionEndAction.Capture
            : SessionEndAction.None,
        _ => SessionEndAction.None,
    };

    /// <summary>
    /// <see cref="Classify"/> plus the one side effect: run the capture
    /// when the classification says the session is really ending. Returns
    /// the classification so the caller can decide whether to consume the
    /// message.
    /// </summary>
    internal SessionEndAction Dispatch(uint msg, IntPtr wParam)
    {
        var action = Classify(msg, wParam);
        if (action == SessionEndAction.Capture) _capture();
        return action;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // TRUE: nothing to veto. Consuming it here also means a session
            // end never reaches WinUI's own handler with a question it has
            // no answer for.
            if (Dispatch(msg, wParam) == SessionEndAction.Allow)
                return new IntPtr(1);
        }
        catch
        {
            // Swallow, exactly as WindowsSystemMenuHook does for the same
            // reason: this proc is called by user32 across the native
            // boundary, a managed exception unwinding into it is undefined
            // behaviour, and a session end is the worst possible moment to
            // take the process down. Chaining below is the safe fallback.
        }

        return _oldProc != IntPtr.Zero
            ? CallWindowProcW(_oldProc, hWnd, msg, wParam, lParam)
            : DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        // Only restore if our proc is still installed; otherwise we would
        // clobber a proc chained on top of ours.
        if (GetWindowLongPtrW(_hwnd, GWLP_WNDPROC) == _procPtr)
            SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, _oldProc);
        _hwnd = IntPtr.Zero;
        _oldProc = IntPtr.Zero;
    }

    // ----- hand-written P/Invoke ----------------------------------------
    // Hand-written (not CsWin32) so the WndProc-as-IntPtr subclassing shape
    // stays obvious and local, matching SysCharBeepSuppressor and
    // WindowsGlobalHotKey. The callback is passed as a function pointer
    // (not a marshalled delegate parameter) for the same reason those files
    // do it.

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CallWindowProcW(
        IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}