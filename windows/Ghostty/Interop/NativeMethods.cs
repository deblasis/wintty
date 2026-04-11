// P/Invoke layer for libghostty (ghostty.dll).
//
// This mirrors include/ghostty.h. It is intentionally incremental: the shell
// only needs a subset of the C API (init, config, app lifecycle, surface
// lifecycle, input, size, focus) to bring up the first window. Additional
// functions will be added as features land. Keep this file sorted in the
// same order as ghostty.h so drift is easy to spot on rebases.

using System;
using System.Runtime.InteropServices;
using Ghostty.Core.Interop;

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
    // Zig bool is 1 byte; use byte to avoid SYSLIB1051 without
    // assembly-wide DisableRuntimeMarshalling.
    private byte _composing;
    public bool Composing
    {
        readonly get => _composing != 0;
        set => _composing = value ? (byte)1 : (byte)0;
    }
}

// GhosttySharedTextureConfig and GhosttySharedTextureSnapshot live in
// Ghostty.Core/Interop/GhosttySharedTexture.cs so the test project can
// pin their layouts without depending on the WinUI 3 app project.

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyPlatformWindows
{
    public IntPtr Hwnd;                        // null for composition mode
    public IntPtr SwapChainPanel;              // ISwapChainPanelNative*
    public GhosttySharedTextureConfig SharedTexture;
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
    private byte _waitAfterCommand; // Zig bool → byte (see GhosttyInputKey)
    public bool WaitAfterCommand
    {
        readonly get => _waitAfterCommand != 0;
        set => _waitAfterCommand = value ? (byte)1 : (byte)0;
    }
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

// GhosttyActionTag, GhosttyActionScrollbar, GhosttyProgressState, and
// GhosttyActionProgressReport now live in Ghostty.Core.Interop so
// Ghostty.Tests (pure net9.0, no WinAppSDK) can pin their ordinals and
// struct sizes. Call sites in GhosttyHost see them unchanged via the
// `using Ghostty.Core.Interop;` at the top of this file.
//
// See GhosttyActionsLayoutTests in Ghostty.Tests for the build-time
// assertions and the grep command for re-verifying against
// include/ghostty.h after a libghostty rebase.

// ghostty_action_set_title_s { const char* title; }
// We only read .title; the struct is declared so the offset is explicit.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionSetTitle
{
    public IntPtr Title;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyDiagnostic
{
    public IntPtr Message;  // const char*
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyString
{
    public IntPtr Ptr;      // const char*
    public UIntPtr Len;
    // Zig c.String has a trailing bool (1 byte) for sentinel, but we
    // only use Ptr and Len. The struct has trailing padding to pointer
    // alignment so the bool sits in padding space on x64.
    private byte _sentinel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyInputTrigger
{
    public int Tag;         // 0=physical, 1=unicode, 2=catch_all
    public uint Key;        // union: translated, physical, or unicode
    public uint Mods;       // modifier flags
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyRuntimeConfig
{
    public IntPtr Userdata;
    private byte _supportsSelectionClipboard; // Zig bool → byte (see GhosttyInputKey)
    public bool SupportsSelectionClipboard
    {
        readonly get => _supportsSelectionClipboard != 0;
        set => _supportsSelectionClipboard = value ? (byte)1 : (byte)0;
    }
    public IntPtr WakeupCb;              // function pointers held as IntPtr
    public IntPtr ActionCb;              // so we control the lifetime of the
    public IntPtr ReadClipboardCb;       // managed delegates they point at.
    public IntPtr ConfirmReadClipboardCb;
    public IntPtr WriteClipboardCb;
    public IntPtr CloseSurfaceCb;
}

internal static partial class NativeMethods
{
    // Name only, no extension or path. .NET's native loader handles the
    // platform-specific suffix and searches the app base directory first,
    // so the copy-on-build from zig-out in the csproj lands it where we
    // need it. Users who want to point at a debug build can set
    // DllImportResolver in App.xaml.cs.
    private const string Dll = "ghostty";

    // ---- init ----------------------------------------------------------

    [LibraryImport(Dll, EntryPoint = "ghostty_init")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial int Init(UIntPtr argc, IntPtr argv);

    // ---- config --------------------------------------------------------

    [LibraryImport(Dll, EntryPoint = "ghostty_config_new")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial GhosttyConfig ConfigNew();

    [LibraryImport(Dll, EntryPoint = "ghostty_config_free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void ConfigFree(GhosttyConfig config);

    [LibraryImport(Dll, EntryPoint = "ghostty_config_load_default_files")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void ConfigLoadDefaultFiles(GhosttyConfig config);

    [LibraryImport(Dll, EntryPoint = "ghostty_config_finalize")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void ConfigFinalize(GhosttyConfig config);

    [LibraryImport(Dll, EntryPoint = "ghostty_config_clone")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial GhosttyConfig ConfigClone(GhosttyConfig config);

    [LibraryImport(Dll, EntryPoint = "ghostty_config_get")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool ConfigGet(GhosttyConfig config, IntPtr output, IntPtr key, UIntPtr keyLen);

    [LibraryImport(Dll, EntryPoint = "ghostty_config_trigger")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial GhosttyInputTrigger ConfigTrigger(GhosttyConfig config, IntPtr action, UIntPtr actionLen);

    [LibraryImport(Dll, EntryPoint = "ghostty_config_diagnostics_count")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial uint ConfigDiagnosticsCount(GhosttyConfig config);

    [LibraryImport(Dll, EntryPoint = "ghostty_config_get_diagnostic")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial GhosttyDiagnostic ConfigGetDiagnostic(GhosttyConfig config, uint index);

    [LibraryImport(Dll, EntryPoint = "ghostty_config_open_path")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial GhosttyString ConfigOpenPath();

    // ---- app -----------------------------------------------------------

    [LibraryImport(Dll, EntryPoint = "ghostty_app_new")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial GhosttyApp AppNew(in GhosttyRuntimeConfig runtime, GhosttyConfig config);

    [LibraryImport(Dll, EntryPoint = "ghostty_app_free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void AppFree(GhosttyApp app);

    [LibraryImport(Dll, EntryPoint = "ghostty_app_tick")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void AppTick(GhosttyApp app);

    [LibraryImport(Dll, EntryPoint = "ghostty_app_set_focus")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void AppSetFocus(GhosttyApp app, [MarshalAs(UnmanagedType.I1)] bool focused);

    [LibraryImport(Dll, EntryPoint = "ghostty_app_set_color_scheme")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void AppSetColorScheme(GhosttyApp app, GhosttyColorScheme scheme);

    [LibraryImport(Dll, EntryPoint = "ghostty_app_update_config")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void AppUpdateConfig(GhosttyApp app, GhosttyConfig config);

    // ---- surface lifecycle ---------------------------------------------

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_config_new")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial GhosttySurfaceConfig SurfaceConfigNew();

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_new")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial GhosttySurface SurfaceNew(GhosttyApp app, in GhosttySurfaceConfig config);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceFree(GhosttySurface surface);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_refresh")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceRefresh(GhosttySurface surface);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_draw")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceDraw(GhosttySurface surface);

    // ---- surface size / scale / focus ----------------------------------

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_set_size")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceSetSize(GhosttySurface surface, uint width, uint height);

    // MapVirtualKeyW for the keycode ScanCode==0 fallback in TerminalControl.
    // Win32 user32, not WinRT - bypasses any WinUI framework filtering.
    internal const uint MAPVK_VK_TO_VSC = 0;
    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    internal static partial uint MapVirtualKeyW(uint uCode, uint uMapType);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_set_content_scale")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceSetContentScale(GhosttySurface surface, double x, double y);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_set_focus")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceSetFocus(GhosttySurface surface, [MarshalAs(UnmanagedType.I1)] bool focused);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_set_occlusion")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceSetOcclusion(GhosttySurface surface, [MarshalAs(UnmanagedType.I1)] bool occluded);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_size")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial GhosttySurfaceSize SurfaceSize(GhosttySurface surface);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_shared_texture")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SurfaceSharedTexture(
        GhosttySurface surface,
        out GhosttySharedTextureSnapshot snapshot);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_surface_shared_texture")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SurfaceSharedTexture(
        GhosttySurface surface,
        out GhosttySharedTextureSnapshot snapshot);

    // ---- surface input -------------------------------------------------

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_key")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SurfaceKey(GhosttySurface surface, GhosttyInputKey key);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_text")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceText(GhosttySurface surface, IntPtr utf8, UIntPtr len);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_preedit")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfacePreedit(GhosttySurface surface, IntPtr utf8, UIntPtr len);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_mouse_button")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SurfaceMouseButton(
        GhosttySurface surface,
        GhosttyMouseState state,
        GhosttyMouseButton button,
        GhosttyMods mods);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_mouse_pos")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceMousePos(GhosttySurface surface, double x, double y, GhosttyMods mods);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_mouse_scroll")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceMouseScroll(GhosttySurface surface, double x, double y, int scrollMods);

    // ghostty_surface_binding_action takes a non-NUL-terminated UTF-8
    // string plus length and runs it through input.Binding.Action.parse.
    // Used to forward ScrollBar drag events back into libghostty as a
    // "scroll_to_row:N" binding action — the same path GTK uses from
    // its vadjustment value-changed signal (see src/apprt/gtk/class/
    // surface.zig::vadjValueChanged).
    // Raw-pointer overload for zero-alloc hot paths (scrollbar drag):
    // caller owns the UTF-8 buffer (typically stackalloc'd) and passes
    // its length in bytes. No NUL terminator required on the native
    // side — libghostty takes (ptr, len).
    [LibraryImport(Dll, EntryPoint = "ghostty_surface_binding_action")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static unsafe partial bool SurfaceBindingAction(
        GhosttySurface surface,
        byte* action,
        UIntPtr actionLen);

    // ---- surface misc --------------------------------------------------

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_request_close")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceRequestClose(GhosttySurface surface);

    [LibraryImport(Dll, EntryPoint = "ghostty_surface_process_exited")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SurfaceProcessExited(GhosttySurface surface);

    // ghostty_surface_complete_clipboard_request(surface, text, state, confirmed)
    // Called once per read/confirm request to return clipboard text to libghostty
    // and release its internal request state. Must be called exactly once even on
    // error paths -- skipping it leaks state inside libghostty.
    [LibraryImport(Dll, EntryPoint = "ghostty_surface_complete_clipboard_request",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceCompleteClipboardRequest(
        IntPtr surface,
        string text,
        IntPtr state,
        [MarshalAs(UnmanagedType.I1)] bool confirmed);

    // ghostty_surface_complete_clipboard_request(surface, text, state, confirmed)
    // Called once per read/confirm request to return clipboard text to libghostty
    // and release its internal request state. Must be called exactly once even on
    // error paths -- skipping it leaks state inside libghostty.
    //
    // Source-generated via [LibraryImport] so this entry point is AOT-friendly
    // and produces no IL stub. The rest of the file still uses [DllImport]; the
    // standing migration TODO covers those.
    [LibraryImport(Dll, EntryPoint = "ghostty_surface_complete_clipboard_request",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void SurfaceCompleteClipboardRequest(
        IntPtr surface,
        string text,
        IntPtr state,
        [MarshalAs(UnmanagedType.I1)] bool confirmed);

    // ---- user32 --------------------------------------------------------

    // MessageBeep is thread-safe and minimal-dependency. Used by the
    // action callback for RING_BELL without any dispatcher hop.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-messagebeep
    internal const uint MB_OK = 0x00000000;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] // Win32 BOOL is 4 bytes, not 1
    internal static partial bool MessageBeep(uint uType);
}
