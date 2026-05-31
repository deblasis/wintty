using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Ghostty.Hosting;

/// <summary>
/// Installs a thread-level <c>WH_KEYBOARD</c> hook on the UI thread to
/// catch the Alt+Space chord and open the standard window system menu
/// (Restore / Move / Size / Minimize / Maximize / Close) for the active
/// window, matching Windows Terminal.
///
/// Why a hook rather than a window proc or a XAML key handler: in this
/// custom-title-bar WinUI 3 app the Alt+Space WM_SYSKEYDOWN is consumed
/// by WinUI's input pre-translate before any window procedure (top-level
/// or InputSite child) or <c>TerminalControl.OnKeyDown</c> ever sees it.
/// A thread keyboard hook runs inside the message-retrieval call, ahead
/// of that pre-translate, so it is the only place the chord is reachable.
///
/// Even the hook does not see the Space key-DOWN -- the framework still
/// swallows it upstream -- so the menu is opened on the Space key-UP edge
/// (Alt is still held there, so the chord is still identifiable). Every
/// matching edge is discarded (the hook returns non-zero), which also
/// stops the WM_SYSCHAR the system would otherwise turn into the default
/// menu beep.
///
/// The UI thread serves every window in the process, so a single
/// thread-scoped hook covers them all; this is an app-wide singleton
/// (one instance, constructed in App.OnLaunched).
/// </summary>
internal sealed partial class WindowsSystemMenuHook : IDisposable
{
    private const int WH_KEYBOARD = 2;
    private const int HC_ACTION = 0;
    private const int VK_SPACE = 0x20;
    private const int VK_CONTROL = 0x11;

    // WH_KEYBOARD lParam flags (the keyboard-message lParam layout):
    //   bit 29 (0x20000000) context code  -> ALT is held
    //   bit 31 (0x80000000) transition     -> key is being released
    private const uint AltDownFlag = 0x2000_0000;
    private const uint KeyUpFlag = 0x8000_0000;

    // The hook proc is an UnmanagedCallersOnly function pointer and so
    // cannot capture instance state; route through a single static
    // instance. Only one instance is ever created (App.OnLaunched) and
    // only the UI thread sets or reads it, so there is no race to guard.
    private static WindowsSystemMenuHook? s_instance;

    private readonly DispatcherQueue _dispatcher;
    private readonly Action<nint> _onChord;
    private nint _hook;

    /// <param name="onChord">
    /// Invoked on the UI thread with the HWND that had focus when the
    /// chord fired (captured at hook time, not re-queried later).
    /// </param>
    public WindowsSystemMenuHook(DispatcherQueue dispatcher, Action<nint> onChord)
    {
        _dispatcher = dispatcher;
        _onChord = onChord;
    }

    public unsafe void Enable()
    {
        if (_hook != 0) return;
        s_instance = this;

        // hmod == NULL with the current thread id installs a hook scoped
        // to this (UI) thread, with the proc living in this process.
        _hook = SetWindowsHookEx(
            WH_KEYBOARD,
            &HookProc,
            IntPtr.Zero,
            GetCurrentThreadId());
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint HookProc(int code, nuint wParam, nint lParam)
    {
        // Per the HOOKPROC contract, a negative code means "do not process,
        // just forward."
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        // This proc is called by user32 across the native boundary; a
        // managed exception unwinding into it is undefined behavior, so
        // never let one escape (mirrors the GhosttyHost native-callback
        // guards). On any failure we fall through to the default chain.
        try
        {
            if (code == HC_ACTION
                && s_instance is { } self
                && (int)wParam == VK_SPACE)
            {
                var flags = (uint)lParam;
                // Require ALT held and CTRL up. The CTRL-up check excludes
                // both Ctrl+Alt+Space and AltGr+Space (AltGr reports CTRL
                // down), so only a bare Alt+Space opens the menu and AltGr
                // text input is never disturbed.
                if ((flags & AltDownFlag) != 0 && (GetKeyState(VK_CONTROL) & 0x8000) == 0)
                {
                    // Fire on the Space key-UP only: the framework swallows
                    // the key-DOWN before the hook chain, so the release
                    // edge is the one we get. Discard every matching edge
                    // (return 1) so the framework never turns it into the
                    // default menu beep.
                    if ((flags & KeyUpFlag) != 0)
                    {
                        // Capture the target window now, on the UI thread,
                        // rather than re-querying the foreground window from
                        // the queued callback (focus could move in between).
                        var target = GetForegroundWindow();
                        // TrackPopupMenu runs a modal loop and must run on
                        // the UI thread; it must not block the hook chain,
                        // so marshal it onto the dispatcher.
                        self._dispatcher.TryEnqueue(() => self._onChord(target));
                    }

                    return 1; // non-zero: discard the keystroke
                }
            }
        }
        catch
        {
            // Swallow: forwarding to the next hook below is the safe
            // fallback, and there is nothing actionable to log from here.
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }

    // Hand-written P/Invoke: SetWindowsHookEx's function-pointer
    // parameter and the hook-chain calls are simpler to express directly
    // than through the CsWin32 HOOKPROC delegate, and the function-pointer
    // form is NativeAOT-friendly.
    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint SetWindowsHookEx(
        int idHook,
        delegate* unmanaged[Stdcall]<int, nuint, nint, nint> lpfn,
        IntPtr hmod,
        uint dwThreadId);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetForegroundWindow();

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial short GetKeyState(int nVirtKey);
}
