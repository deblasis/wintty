using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.Win32;

namespace Ghostty.Hosting;

/// <summary>
/// Owns a message-only Win32 window plus a single registered hotkey.
/// When the chord fires, Windows posts WM_HOTKEY to the message-only
/// window; the WndProc forwards a managed event to the UI thread.
///
/// Intended to be constructed once at app startup (in App.OnLaunched)
/// and torn down on app exit. Re-registration via <see cref="Register"/>
/// replaces the previous chord, so config reloads can rebind without
/// recreating the service. Returning <c>false</c> from <see cref="Register"/>
/// means another process already holds the chord; the caller is
/// expected to log and degrade (the app stays usable; just no quake hotkey).
///
/// We hand-write the Win32 surface for window creation / window proc
/// dispatch because CsWin32's WNDPROC delegate type does not compose
/// well with the message-only window pattern (the generated WNDCLASSW
/// expects a function pointer field whose marshalling differs from the
/// classic delegate-as-IntPtr approach). RegisterHotKey / UnregisterHotKey
/// are sourced from CsWin32 (see NativeMethods.txt).
/// </summary>
internal sealed partial class WindowsGlobalHotKey : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const int HWND_MESSAGE = -3;

    // Single unique class name per process. The class lives until process
    // exit either way (we do not UnregisterClass on Dispose), so reusing
    // it across hypothetical multiple instances is safe.
    private const string ClassName = "WinttyGlobalHotKeyWindow";

    // Static one-shot class registration. WNDCLASS registration is
    // idempotent per-class-name within a process, but the underlying
    // Win32 API returns 0 with ERROR_CLASS_ALREADY_EXISTS on the second
    // attempt, which we would have to treat as a non-error. Guarding here
    // is simpler.
    private static bool s_classRegistered;
    private static readonly object s_classLock = new();

    // Single pinned delegate per process. RegisterClassExW captures the
    // function pointer into the class definition; future window creations
    // for the same class use the same pointer. Keeping a static ref
    // prevents the GC from collecting it while the class is alive (i.e.
    // for the rest of the process lifetime).
    private static WndProcDelegate? s_wndProc;

    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<WindowsGlobalHotKey> _logger;

    private IntPtr _hwnd;
    private int _currentId;

    // Per-process monotonic id for RegisterHotKey. Starts at 1 because 0
    // is a valid id but pairs awkwardly with our "zero means unregistered"
    // sentinel for _currentId.
    private static int s_nextId = 1;

    // Static map from message-only HWND to the instance whose Pressed
    // event should fire. The WndProc is necessarily static (it is the
    // class-level handler), so this is the only way to route a WM_HOTKEY
    // back to the right instance. Today we only ever create one instance,
    // but the indirection keeps the door open without locking us into a
    // singleton.
    private static readonly System.Collections.Generic.Dictionary<IntPtr, WindowsGlobalHotKey> s_byHwnd = new();
    private static readonly object s_byHwndLock = new();

    public WindowsGlobalHotKey(
        DispatcherQueue dispatcher,
        ILogger<WindowsGlobalHotKey> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatcher = dispatcher;
        _logger = logger;

        EnsureClassRegistered();

        // Message-only window: parent = HWND_MESSAGE, all geometry zeroed.
        // Style 0 and ex-style 0; it never paints and never participates
        // in window enumeration, focus, or hit-testing.
        _hwnd = CreateWindowExW(
            dwExStyle: 0,
            lpClassName: ClassName,
            lpWindowName: "",
            dwStyle: 0,
            X: 0, Y: 0, nWidth: 0, nHeight: 0,
            hWndParent: (IntPtr)HWND_MESSAGE,
            hMenu: IntPtr.Zero,
            hInstance: IntPtr.Zero,
            lpParam: IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            // CreateWindowExW failure is exceptional (would only happen
            // under extreme handle exhaustion or class lookup failure).
            // Throw rather than silently producing a service that can
            // never fire; the caller can catch and log if needed.
            throw new InvalidOperationException(
                $"WindowsGlobalHotKey: CreateWindowExW failed (LastWin32Error={Marshal.GetLastWin32Error()})");
        }

        lock (s_byHwndLock)
        {
            s_byHwnd[_hwnd] = this;
        }
    }

    /// <summary>
    /// Raised on the UI thread when the registered chord fires. May fire
    /// repeatedly while the key is held (Windows generates a WM_HOTKEY
    /// for each key repeat); the handler should debounce if that matters.
    /// </summary>
    public event EventHandler? Pressed;

    /// <summary>
    /// Try to register the (modifiers, vk) hotkey. Returns false if
    /// another process already grabbed the chord. Idempotent: a second
    /// call replaces the previous registration so config reloads can
    /// rebind without recreating the service.
    /// </summary>
    public bool Register(uint modifiers, uint virtualKey)
    {
        if (_hwnd == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(WindowsGlobalHotKey));

        // Drop the previous registration before claiming the new chord.
        // Skipping this would leak the slot if Register is called twice
        // with different chords.
        if (_currentId != 0)
        {
            PInvoke.UnregisterHotKey(new Windows.Win32.Foundation.HWND(_hwnd), _currentId);
            _currentId = 0;
        }

        var id = System.Threading.Interlocked.Increment(ref s_nextId);
        if (!PInvoke.RegisterHotKey(
            new Windows.Win32.Foundation.HWND(_hwnd),
            id,
            (Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS)modifiers,
            virtualKey))
        {
            _logger.LogRegisterFailed(modifiers, virtualKey);
            return false;
        }

        _currentId = id;
        return true;
    }

    /// <summary>
    /// Drop the current registration if any. Safe to call repeatedly.
    /// </summary>
    public void Unregister()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_currentId == 0) return;
        PInvoke.UnregisterHotKey(new Windows.Win32.Foundation.HWND(_hwnd), _currentId);
        _currentId = 0;
    }

    public void Dispose()
    {
        Unregister();
        if (_hwnd != IntPtr.Zero)
        {
            lock (s_byHwndLock)
            {
                s_byHwnd.Remove(_hwnd);
            }
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    // ----- private helpers ----------------------------------------------

    private static void EnsureClassRegistered()
    {
        lock (s_classLock)
        {
            if (s_classRegistered) return;

            // Hold the delegate in a static field so the GC keeps it
            // alive for the lifetime of the WNDCLASS (i.e. forever).
            s_wndProc = StaticWndProc;
            var fnPtr = Marshal.GetFunctionPointerForDelegate(s_wndProc);

            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = fnPtr,
                hInstance = IntPtr.Zero,
                lpszClassName = Marshal.StringToHGlobalUni(ClassName),
            };

            try
            {
                var atom = RegisterClassExW(in wc);
                if (atom == 0)
                {
                    throw new InvalidOperationException(
                        $"WindowsGlobalHotKey: RegisterClassExW failed (LastWin32Error={Marshal.GetLastWin32Error()})");
                }
                s_classRegistered = true;
            }
            finally
            {
                // The class name string is copied into Win32 internals,
                // so we can free the unmanaged buffer immediately.
                if (wc.lpszClassName != IntPtr.Zero)
                    Marshal.FreeHGlobal(wc.lpszClassName);
            }
        }
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            WindowsGlobalHotKey? instance = null;
            lock (s_byHwndLock)
            {
                s_byHwnd.TryGetValue(hWnd, out instance);
            }
            if (instance is not null)
            {
                // Marshal to the UI thread so handlers can touch XAML.
                // WM_HOTKEY itself is dispatched on the thread that owns
                // the message-only window (i.e. the UI thread, since the
                // window is created from the UI thread), but we route
                // through TryEnqueue anyway so handlers can rely on a
                // consistent dispatcher context regardless of who pumped
                // the message.
                var dispatcher = instance._dispatcher;
                dispatcher.TryEnqueue(() => instance.Pressed?.Invoke(instance, EventArgs.Empty));
            }
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ----- hand-written P/Invoke ----------------------------------------

    // Hand-written rather than sourced from CsWin32 because the WNDCLASSEX
    // shape we need (lpfnWndProc as IntPtr from
    // Marshal.GetFunctionPointerForDelegate) does not match the strongly-
    // typed WNDPROC delegate that CsWin32 generates. Keeping these private
    // and local-to-this-file makes the hand-written surface obvious.

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int X, int Y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial ushort RegisterClassExW(in WNDCLASSEX wc);
}

internal static partial class WindowsGlobalHotKeyLogExtensions
{
    // Warning, not Error: a failed registration just means the global
    // chord is unavailable. The app still launches and every per-window
    // chord still works.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Hosting.HotKeyRegisterFailed,
                   Level = LogLevel.Warning,
                   Message = "[WindowsGlobalHotKey] RegisterHotKey failed (mods={Modifiers:X}, vk={VirtualKey:X}); chord unavailable")]
    internal static partial void LogRegisterFailed(
        this ILogger<WindowsGlobalHotKey> logger, uint modifiers, uint virtualKey);
}
