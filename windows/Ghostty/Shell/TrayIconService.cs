using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Ghostty.Shell;

/// <summary>
/// Notification-area icon for Wintty. Double-click focuses a terminal
/// window; right-click offers Show and Exit. Uses Shell_NotifyIconW with
/// a message-only window, matching <see cref="Hosting.WindowsGlobalHotKey"/>'s
/// static WndProc + pinned delegate pattern so NativeAOT does not collect
/// the class proc.
/// </summary>
internal sealed unsafe partial class TrayIconService : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;
    private const int NIM_ADD = 0;
    private const int NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x0001;
    private const int NIF_ICON = 0x0002;
    private const int NIF_TIP = 0x0004;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int HWND_MESSAGE = -3;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint IDM_SHOW = 1001;
    private const uint IDM_EXIT = 1002;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    private const string ClassName = "WinttyTrayIconWindow";

    private static bool s_classRegistered;
    private static readonly object s_classLock = new();
    private static WndProcDelegate? s_wndProc;
    private static readonly Dictionary<IntPtr, TrayIconService> s_byHwnd = new();
    private static readonly object s_byHwndLock = new();

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueHandler _showWindows;
    private readonly DispatcherQueueHandler _exitApplication;
    private IntPtr _messageHwnd;
    private IntPtr _icon;
    private bool _added;

    internal TrayIconService(
        DispatcherQueue dispatcher,
        Action showWindows,
        Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(showWindows);
        ArgumentNullException.ThrowIfNull(exitApplication);
        _dispatcher = dispatcher;
        _showWindows = () => showWindows();
        _exitApplication = () => exitApplication();

        EnsureClassRegistered();

        _messageHwnd = CreateWindowExW(
            0, ClassName, "", 0,
            0, 0, 0, 0,
            (IntPtr)HWND_MESSAGE,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_messageHwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"TrayIconService: CreateWindowExW failed (LastWin32Error={Marshal.GetLastPInvokeError()})");
        }

        lock (s_byHwndLock)
            s_byHwnd[_messageHwnd] = this;

        _icon = LoadTrayIcon();
        AddIcon();
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = BuildNotifyData();
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        if (_messageHwnd != IntPtr.Zero)
        {
            lock (s_byHwndLock)
                s_byHwnd.Remove(_messageHwnd);
            DestroyWindow(_messageHwnd);
            _messageHwnd = IntPtr.Zero;
        }
    }

    private static void EnsureClassRegistered()
    {
        lock (s_classLock)
        {
            if (s_classRegistered) return;

            s_wndProc ??= StaticWndProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                lpszClassName = Marshal.StringToHGlobalUni(ClassName),
            };

            try
            {
                if (RegisterClassExW(in wc) == 0)
                {
                    throw new InvalidOperationException(
                        $"TrayIconService: RegisterClassExW failed (LastWin32Error={Marshal.GetLastPInvokeError()})");
                }
                s_classRegistered = true;
            }
            finally
            {
                if (wc.lpszClassName != IntPtr.Zero)
                    Marshal.FreeHGlobal(wc.lpszClassName);
            }
        }
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            TrayIconService? instance = null;
            lock (s_byHwndLock)
                s_byHwnd.TryGetValue(hWnd, out instance);

            if (instance is not null)
            {
                switch ((uint)lParam)
                {
                    case WM_LBUTTONDBLCLK:
                        instance._dispatcher.TryEnqueue(instance._showWindows);
                        return IntPtr.Zero;
                    case WM_RBUTTONUP:
                        // Modal menu must run on the UI thread, not inside WndProc.
                        instance._dispatcher.TryEnqueue(instance.ShowContextMenu);
                        return IntPtr.Zero;
                }
            }
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private IntPtr LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "wintty.ico");
        if (!File.Exists(iconPath))
            return IntPtr.Zero;
        return LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
    }

    private void AddIcon()
    {
        if (_messageHwnd == IntPtr.Zero) return;
        var data = BuildNotifyData();
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private NOTIFYICONDATAW BuildNotifyData()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _messageHwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon,
        };
        var tip = Ghostty.Core.AppIdentity.ProductName;
        var n = Math.Min(tip.Length, 127);
        for (var i = 0; i < n; i++)
            data.szTip[i] = tip[i];
        data.szTip[n] = '\0';
        return data;
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, MF_STRING, IDM_SHOW, "Show Wintty");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, IDM_EXIT, "Exit");
            GetCursorPos(out var pt);
            // TrackPopupMenu requires the owning window to be foreground
            // (see SystemMenuPopup.Track / user32 docs).
            SetForegroundWindow(_messageHwnd);
            var cmd = TrackPopupMenu(
                menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                pt.X, pt.Y, 0, _messageHwnd, IntPtr.Zero);
            if (cmd == IDM_SHOW)
                _dispatcher.TryEnqueue(_showWindows);
            else if (cmd == IDM_EXIT)
                _dispatcher.TryEnqueue(_exitApplication);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // Hand-written LibraryImport (not DllImport): runtime marshalling is
    // disabled for NativeAOT, so SetLastError on classic P/Invoke throws
    // MarshalDirectiveException at first call (see WindowsGlobalHotKey).

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial ushort RegisterClassExW(in WNDCLASSEX lpwcx);

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

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr LoadImageW(
        IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [LibraryImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [LibraryImport("user32.dll", EntryPoint = "CreatePopupMenu", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(
        IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "TrackPopupMenu", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint TrackPopupMenu(
        IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [LibraryImport("user32.dll", EntryPoint = "DestroyMenu", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(IntPtr hMenu);
}
