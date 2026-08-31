using System.Runtime.InteropServices;

namespace Ghostty.Core.Interop;

// Layout types mirroring the ghostty_action_* subset dispatched by the
// Windows apprt. They live in Ghostty.Core (pure net9.0, no WinAppSDK)
// so unit tests can assert ordinal values and struct sizes without
// dragging PRI/MRT into the test project. Ghostty/Interop/NativeMethods.cs
// imports these via `using Ghostty.Core.Interop;` so existing call sites
// in GhosttyHost compile unchanged.
//
// GhosttyActionTagHeaderParityTests reads include/ghostty.h and checks
// every ordinal below against it; GhosttyStructHeaderParityTests computes
// the C layout of each struct below and checks offsets, sizes, field
// types and field names against it. Nothing here needs re-verifying by
// hand after a sync.

// Subset of ghostty_action_tag_e that the Windows apprt dispatches on.
// Any unlisted tag falls through to "return false" in
// GhosttyHost.OnAction.
//
// These are positions in an enum upstream edits, and an insertion shifts
// every later tag without breaking a single compile: the header is not
// compiled here and the tags arrive as ints. Upstream adding
// SET_WINDOW_TITLE at 35 shifted twenty of them, so libghostty's
// first_render arrived as CustomShaderFailed and every new surface raised
// a custom-shader notice. Only the header can contradict this list.
internal enum GhosttyActionTag
{
    NewTab = 2,
    CloseTab = 3,
    NewSplit = 4,
    CloseAllWindows = 5,
    ToggleFullscreen = 7,
    ToggleQuickTerminal = 10,
    ToggleCommandPalette = 11,
    ToggleVisibility = 12,
    ToggleBackgroundOpacity = 13,
    MoveTab = 14,
    GotoTab = 15,
    GotoSplit = 16,
    GotoWindow = 17,
    ResizeSplit = 18,
    EqualizeSplits = 19,
    ToggleSplitZoom = 20,
    PresentTerminal = 21,
    SizeLimit = 22,
    ResetWindowSize = 23,
    InitialSize = 24,
    Scrollbar = 26,
    Inspector = 28,
    DesktopNotification = 32,
    SetTitle = 33,
    SetTabTitle = 34,
    // Not dispatched: no window-title override UI yet. Listed so the
    // ordinal it displaced is visible rather than an unexplained gap.
    SetWindowTitle = 35,
    PromptTitle = 36,
    // The shell reported its directory (OSC 7). Recorded on the pane so
    // duplicate tab and restore respawn the shell where it actually was.
    Pwd = 37,
    MouseShape = 38,
    MouseVisibility = 39,
    MouseOverLink = 40,
    OpenConfig = 42,
    FloatWindow = 44,
    ReloadConfig = 49,
    ConfigChange = 50,
    CloseWindow = 51,
    RingBell = 52,
    SelectionChanged = 53,
    ShowChildExited = 58,
    ProgressReport = 59,
    StartSearch    = 62,
    EndSearch      = 63,
    SearchTotal    = 64,
    SearchSelected = 65,
    PromptReady    = 69,
    FirstRender    = 70,
    CustomShaderFailed = 71,
    // Fork-appended tail tags (Windows-apprt tab-shell actions). Appended
    // rather than inserted so every upstream ordinal stays stable; the
    // parity test against include/ghostty.h holds the positions.
    PinTab = 72,
    UnpinTab = 73,
    MoveGroup = 74,
}

// ghostty_target_tag_e: which half of OnAction the action is addressed to.
// Lives here rather than as consts beside the switch so a test can reach it:
// swapping these two routes every app action into the surface arm.
internal enum GhosttyTargetTag { App = 0, Surface = 1 }

// ghostty_action_scrollbar_s:
//   { uint64 total; uint64 offset; uint64 len; }
// All values are row counts. `total` is scrollback+viewport, `offset`
// is the top visible row, `len` is the visible row count. The bar is
// "at rest" / unnecessary when total <= len.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionScrollbar
{
    public ulong Total;
    public ulong Offset;
    public ulong Len;
}

// ghostty_action_mouse_over_link_s:
//   { const char* url; size_t len; }
// On x64: 16 bytes total (8 + 8). Read at actionPtr + 8 inside OnAction;
// url=null+len=0 means "pointer left the link" (clear hover state).
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionMouseOverLink
{
    public nint Url;
    public nuint Len;
}

// ghostty_action_split_direction_e: where the new split is placed
// relative to the focused surface.
internal enum GhosttySplitDirection { Right = 0, Down = 1, Left = 2, Up = 3 }

// ghostty_action_goto_split_e: the focus-movement variants accept a
// directional sibling; Previous/Next walk tree order, the rest pick a
// spatial neighbour.
internal enum GhosttyGotoSplit { Previous = 0, Next = 1, Up = 2, Left = 3, Down = 4, Right = 5 }

// ghostty_action_resize_split_direction_e. Note this is a *different*
// ordering from GhosttySplitDirection: resize grows the split toward
// the named edge, so the values do not line up with placement.
internal enum GhosttyResizeSplitDirection { Up = 0, Down = 1, Left = 2, Right = 3 }

// ghostty_action_goto_window_e: relative window navigation.
internal enum GhosttyGotoWindow { Previous = 0, Next = 1 }

// ghostty_action_float_window_e: always-on-top state to apply.
internal enum GhosttyFloatWindow { On = 0, Off = 1, Toggle = 2 }

// ghostty_action_prompt_title_e: what the prompt renames. Window arrived in
// the same sync that shifted the tags above and has no window-title override
// to drive here, but it is listed because the handler has to be able to tell
// it from Surface: collapsing the payload to "is it Tab" silently renamed the
// pane instead, and reported the action handled.
internal enum GhosttyPromptTitle { Surface = 0, Tab = 1, Window = 2 }

// ghostty_action_size_limit_s:
//   { uint32 min_width; uint32 min_height; uint32 max_width; uint32 max_height; }
// All values are pixels; 0 means "no limit" for that dimension. On x64
// this is 16 bytes (4 * 4), read at actionPtr+8 via Unsafe.ReadUnaligned.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionSizeLimit
{
    public uint MinWidth;
    public uint MinHeight;
    public uint MaxWidth;
    public uint MaxHeight;
}

// ghostty_action_initial_size_s:
//   { uint32 width; uint32 height; }
// Pixels. 8 bytes on x64. libghostty emits this during surface init
// ONLY when both window-width and window-height are configured
// (src/Surface.zig recomputeInitialSize); it is the canonical source
// for the "return to default size" action and is not itself bindable.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionInitialSize
{
    public uint Width;
    public uint Height;
}

// Sentinel tab targets carried in ghostty_action_goto_tab_s. Positive
// values are 1-based tab indices; these negatives select relative tabs.
internal enum GhosttyGotoTab { Previous = -1, Next = -2, Last = -3 }

// ghostty_action_resize_split_s:
//   { uint16 amount; <enum int> direction; }
// On x64: amount@0, then 2 bytes of padding to align the int-sized
// direction enum to +4, total 8 bytes.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionResizeSplit
{
    public ushort Amount;
    public GhosttyResizeSplitDirection Direction;
}

// ghostty_action_move_tab_s:
//   { ssize_t amount; }
// Stored as `nint` so the layout matches the C ssize_t on both 32- and
// 64-bit builds. Negative moves left, positive moves right.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionMoveTab
{
    public nint Amount;
}

// ghostty_action_move_group_s:
//   { ssize_t amount; }
// Same shape as MoveTab for the group-as-unit move: the signed offset is
// one neighbouring group per step, negative left, positive right. A
// separate mirror (rather than reusing GhosttyActionMoveTab) so the
// struct parity test pins this header typedef by name.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionMoveGroup
{
    public nint Amount;
}

// ghostty_action_desktop_notification_s:
//   { const char* title; const char* body; }
// Both pointer-sized: title@0, body@8, 16 bytes on x64. Read at
// actionPtr+8. Declared so the layout is checked rather than living as
// two literals at the read site.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionDesktopNotification
{
    public nint Title;
    public nint Body;
}

// ghostty_action_progress_report_state_e.
internal enum GhosttyProgressState
{
    Remove = 0,
    Set = 1,
    Error = 2,
    Indeterminate = 3,
    Pause = 4,
}

// ghostty_action_progress_report_s:
//   { ghostty_action_progress_report_state_e state;
//     int8 progress; /* -1 if none, else 0..100 */ }
// State is the enum rather than a bare int, so it mirrors the C field type
// and the read site does not have to cast.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionProgressReport
{
    public GhosttyProgressState State;
    public sbyte Progress;
}

// ghostty_surface_message_childexited_s:
//   { uint32 exit_code; uint64 runtime_ms; }
// On x64: exit_code@0, 4 bytes of padding before the 8-byte-aligned u64,
// runtime_ms@8, total 16 bytes. GhosttyHost reads this at (actionPtr + 8)
// (the union sits at +8, and within it the u64 forces the same +0/+8
// field layout). The C header's field is mis-typed `timetime_ms` but the
// ABI is the u64 at +8 -- the name is irrelevant to the binary layout.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyChildExited
{
    public uint ExitCode;
    public ulong RuntimeMs;
}

// ghostty_action_start_search_s:
//   { const char* needle; }
// All values are pointer-sized. On x64: 8 bytes total. GhosttyHost
// reads at (actionPtr + 8) and decodes the null-terminated UTF-8
// needle via Marshal.PtrToStringUTF8.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionStartSearch
{
    public nint Needle;
}

// ghostty_action_search_total_s:
//   { ssize_t total; }
// All values are pointer-sized. On x64: 8 bytes total. Stored as
// `nint` so the layout matches the C ssize_t on both 32- and 64-bit
// builds; consumers cast to `long` for the SearchState API.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionSearchTotal
{
    public nint Total;
}

// ghostty_action_search_selected_s:
//   { ssize_t selected; }
// All values are pointer-sized. Same shape as SearchTotal; libghostty
// reports -1 (or negative) when no match is selected yet.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionSearchSelected
{
    public nint Selected;
}
