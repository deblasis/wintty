// P/Invoke layer for libghostty (ghostty.dll).
//
// This mirrors include/ghostty.h. It is intentionally incremental: the shell
// only needs a subset of the C API (init, config, app lifecycle, surface
// lifecycle, input, size, focus) to bring up the first window. Additional
// functions will be added as features land. Keep this file sorted in the
// same order as ghostty.h so drift is easy to spot on rebases.

using System;
using System.Runtime.InteropServices;

namespace Ghostty.Interop;

// Opaque handle typedefs from ghostty.h. We use IntPtr so no marshaling
// gymnastics are needed; the C side sees these as void*.
internal readonly record struct GhosttyApp(IntPtr Handle);
internal readonly record struct GhosttyConfig(IntPtr Handle);
internal readonly record struct GhosttySurface(IntPtr Handle);

internal enum GhosttyPlatform
{
    Invalid = 0,
    MacOS = 1,
    IOS = 2,
    Windows = 3,
}

internal enum GhosttySurfaceContext
{
    Window = 0,
    Tab = 1,
    Split = 2,
}

internal enum GhosttyClipboard
{
    Standard = 0,
    Selection = 1,
}

internal enum GhosttyClipboardRequest
{
    Paste = 0,
    Osc52Read = 1,
    Osc52Write = 2,
}

internal enum GhosttyMouseState
{
    Release = 0,
    Press = 1,
}

internal enum GhosttyMouseButton
{
    Unknown = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
    Four = 4,
    Five = 5,
    Six = 6,
    Seven = 7,
    Eight = 8,
    Nine = 9,
    Ten = 10,
    Eleven = 11,
}

internal enum GhosttyColorScheme
{
    Light = 0,
    Dark = 1,
}

[Flags]
internal enum GhosttyMods
{
    None = 0,
    Shift = 1 << 0,
    Ctrl = 1 << 1,
    Alt = 1 << 2,
    Super = 1 << 3,
    Caps = 1 << 4,
    Num = 1 << 5,
    ShiftRight = 1 << 6,
    CtrlRight = 1 << 7,
    AltRight = 1 << 8,
    SuperRight = 1 << 9,
}

internal enum GhosttyInputAction
{
    Release = 0,
    Press = 1,
    Repeat = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyInputKey
{
    public GhosttyInputAction Action;
    public GhosttyMods Mods;
    public GhosttyMods ConsumedMods;
    public uint Keycode;
    public IntPtr Text;              // const char*
    public uint UnshiftedCodepoint;
    [MarshalAs(UnmanagedType.I1)]
    public bool Composing;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyPlatformWindows
{
    public IntPtr Hwnd;               // null for composition mode
    public IntPtr SwapChainPanel;     // ISwapChainPanelNative*
    public IntPtr SharedTextureOut;   // OUT: HANDLE
    public uint TextureWidth;
    public uint TextureHeight;
}

// ghostty_platform_u — explicit layout so all three variants share memory.
// Windows variant is the widest, so size the struct to match it.
[StructLayout(LayoutKind.Explicit)]
internal struct GhosttyPlatformUnion
{
    [FieldOffset(0)] public IntPtr MacosNsView;     // macos variant
    [FieldOffset(0)] public IntPtr IosUiView;       // ios variant
    [FieldOffset(0)] public GhosttyPlatformWindows Windows;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyEnvVar
{
    public IntPtr Key;    // const char*
    public IntPtr Value;  // const char*
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttySurfaceConfig
{
    public GhosttyPlatform PlatformTag;
    public GhosttyPlatformUnion Platform;
    public IntPtr Userdata;
    public double ScaleFactor;
    public float FontSize;
    public IntPtr WorkingDirectory; // const char*
    public IntPtr Command;          // const char*
    public IntPtr EnvVars;          // ghostty_env_var_s*
    public UIntPtr EnvVarCount;
    public IntPtr InitialInput;     // const char*
    [MarshalAs(UnmanagedType.I1)]
    public bool WaitAfterCommand;
    public GhosttySurfaceContext Context;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttySurfaceSize
{
    public ushort Columns;
    public ushort Rows;
    public uint WidthPx;
    public uint HeightPx;
    public uint CellWidthPx;
    public uint CellHeightPx;
}

// Runtime callback delegates. These are called from the Zig side on its
// own thread; marshal to the UI dispatcher before touching XAML.
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void GhosttyWakeupCb(IntPtr userdata);

// C signature: bool (*)(ghostty_app_t, ghostty_target_s, ghostty_action_s)
// Both ghostty_target_s (16 bytes) and ghostty_action_s (hundreds of bytes)
// are larger than 8 bytes, so on the Windows x64 ABI they are passed by
// hidden pointer. We take both as IntPtr and decode the fields we need
// with Marshal.PtrToStructure / Marshal.ReadIntPtr.
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
internal delegate bool GhosttyActionCb(GhosttyApp app, IntPtr targetPtr, IntPtr actionPtr);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
internal delegate bool GhosttyReadClipboardCb(IntPtr userdata, GhosttyClipboard kind, IntPtr state);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void GhosttyConfirmReadClipboardCb(
    IntPtr userdata,
    IntPtr str,
    IntPtr state,
    GhosttyClipboardRequest request);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void GhosttyWriteClipboardCb(
    IntPtr userdata,
    GhosttyClipboard kind,
    IntPtr content,  // const ghostty_clipboard_content_s*
    UIntPtr count,
    [MarshalAs(UnmanagedType.I1)] bool confirm);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void GhosttyCloseSurfaceCb(
    IntPtr userdata,
    [MarshalAs(UnmanagedType.I1)] bool processAlive);

// Mirrors ghostty_target_s in include/ghostty.h:
//   typedef struct { ghostty_target_tag_e tag; ghostty_target_u target; }
// On x64 the enum is 4 bytes, the union is one 8-byte pointer aligned to 8,
// giving a 4-byte tail pad after the tag. We pin the offsets explicitly so
// this layout cannot drift if the union ever gains a wider variant — we
// would notice via a build failure rather than a silent ABI mismatch.
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct GhosttyTarget
{
    // tag: 0 = app, 1 = surface
    [FieldOffset(0)] public int Tag;
    // union: only the surface variant is populated today
    [FieldOffset(8)] public IntPtr Surface;
}

// Subset of ghostty_action_tag_e from include/ghostty.h that we actually
// dispatch on. Indices are pinned explicitly to the upstream values so a
// reorder upstream cannot silently misroute one tag to another handler -
// any tag we don't list falls through to "return false" in the action
// callback and the core uses its default behavior.
//
// Synced against include/ghostty.h @ 2598bef60. To verify after a rebase:
//   grep -n GHOSTTY_ACTION_ include/ghostty.h | grep -nE 'SET_TITLE|CLOSE_WINDOW|RING_BELL'
// and confirm the ordinal positions still match the values below.
internal enum GhosttyActionTag
{
    SetTitle = 32,
    CloseWindow = 49,
    RingBell = 50,
    ProgressReport = 56,
}

// ghostty_action_progress_report_state_e ordinal values, matching
// the enum in include/ghostty.h around line 850. Do not reorder —
// these are read by value out of the action payload.
internal enum GhosttyProgressState
{
    Remove = 0,
    Set = 1,
    Error = 2,
    Indeterminate = 3,
    Pause = 4,
}

// ghostty_action_progress_report_s:
//   { int32 state; int8 progress; /* -1 if none, else 0..100 */ }
// Marshalled manually in GhosttyHost since the action union layout
// places this at a known offset inside the larger action struct.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionProgressReport
{
    public int State;
    public sbyte Progress;
}

// ghostty_action_set_title_s { const char* title; }
// We only read .title; the struct is declared so the offset is explicit.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionSetTitle
{
    public IntPtr Title;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyRuntimeConfig
{
    public IntPtr Userdata;
    [MarshalAs(UnmanagedType.I1)]
    public bool SupportsSelectionClipboard;
    public IntPtr WakeupCb;              // function pointers held as IntPtr
    public IntPtr ActionCb;              // so we control the lifetime of the
    public IntPtr ReadClipboardCb;       // managed delegates they point at.
    public IntPtr ConfirmReadClipboardCb;
    public IntPtr WriteClipboardCb;
    public IntPtr CloseSurfaceCb;
}

internal static class NativeMethods
{
    // Name only, no extension or path. .NET's native loader handles the
    // platform-specific suffix and searches the app base directory first,
    // so the copy-on-build from zig-out in the csproj lands it where we
    // need it. Users who want to point at a debug build can set
    // DllImportResolver in App.xaml.cs.
    private const string Dll = "ghostty";

    // ---- init ----------------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_init")]
    internal static extern int Init(UIntPtr argc, IntPtr argv);

    // ---- config --------------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_config_new")]
    internal static extern GhosttyConfig ConfigNew();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_config_free")]
    internal static extern void ConfigFree(GhosttyConfig config);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_config_load_default_files")]
    internal static extern void ConfigLoadDefaultFiles(GhosttyConfig config);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_config_finalize")]
    internal static extern void ConfigFinalize(GhosttyConfig config);

    // ---- app -----------------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_app_new")]
    internal static extern GhosttyApp AppNew(in GhosttyRuntimeConfig runtime, GhosttyConfig config);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_app_free")]
    internal static extern void AppFree(GhosttyApp app);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_app_tick")]
    internal static extern void AppTick(GhosttyApp app);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_app_set_focus")]
    internal static extern void AppSetFocus(GhosttyApp app, [MarshalAs(UnmanagedType.I1)] bool focused);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_app_set_color_scheme")]
    internal static extern void AppSetColorScheme(GhosttyApp app, GhosttyColorScheme scheme);

    // ---- surface lifecycle ---------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_config_new")]
    internal static extern GhosttySurfaceConfig SurfaceConfigNew();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_new")]
    internal static extern GhosttySurface SurfaceNew(GhosttyApp app, in GhosttySurfaceConfig config);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_free")]
    internal static extern void SurfaceFree(GhosttySurface surface);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_refresh")]
    internal static extern void SurfaceRefresh(GhosttySurface surface);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_draw")]
    internal static extern void SurfaceDraw(GhosttySurface surface);

    // ---- surface size / scale / focus ----------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_set_size")]
    internal static extern void SurfaceSetSize(GhosttySurface surface, uint width, uint height);

    // MapVirtualKeyW for the keycode ScanCode==0 fallback in TerminalControl.
    // Win32 user32, not WinRT - bypasses any WinUI framework filtering.
    internal const uint MAPVK_VK_TO_VSC = 0;
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    internal static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_set_content_scale")]
    internal static extern void SurfaceSetContentScale(GhosttySurface surface, double x, double y);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_set_focus")]
    internal static extern void SurfaceSetFocus(GhosttySurface surface, [MarshalAs(UnmanagedType.I1)] bool focused);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_set_occlusion")]
    internal static extern void SurfaceSetOcclusion(GhosttySurface surface, [MarshalAs(UnmanagedType.I1)] bool occluded);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_size")]
    internal static extern GhosttySurfaceSize SurfaceSize(GhosttySurface surface);

    // ---- surface input -------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_key")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SurfaceKey(GhosttySurface surface, GhosttyInputKey key);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_text")]
    internal static extern void SurfaceText(GhosttySurface surface, IntPtr utf8, UIntPtr len);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_preedit")]
    internal static extern void SurfacePreedit(GhosttySurface surface, IntPtr utf8, UIntPtr len);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_mouse_button")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SurfaceMouseButton(
        GhosttySurface surface,
        GhosttyMouseState state,
        GhosttyMouseButton button,
        GhosttyMods mods);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_mouse_pos")]
    internal static extern void SurfaceMousePos(GhosttySurface surface, double x, double y, GhosttyMods mods);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_mouse_scroll")]
    internal static extern void SurfaceMouseScroll(GhosttySurface surface, double x, double y, int scrollMods);

    // ---- surface misc --------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_request_close")]
    internal static extern void SurfaceRequestClose(GhosttySurface surface);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_process_exited")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SurfaceProcessExited(GhosttySurface surface);

    // ---- user32 --------------------------------------------------------

    // MessageBeep is thread-safe and minimal-dependency. Used by the
    // action callback for RING_BELL without any dispatcher hop.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-messagebeep
    internal const uint MB_OK = 0x00000000;

    [DllImport("user32.dll", CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MessageBeep(uint uType);
}
