using System.Runtime.InteropServices;

namespace Ghostty.Core.Interop;

// Layout types mirroring the ghostty_action_* subset dispatched by the
// Windows apprt. They live in Ghostty.Core (pure net9.0, no WinAppSDK)
// so unit tests can assert ordinal values and struct sizes without
// dragging PRI/MRT into the test project. Ghostty/Interop/NativeMethods.cs
// imports these via `using Ghostty.Core.Interop;` so existing call sites
// in GhosttyHost compile unchanged.
//
// GhosttyActionsLayoutTests in Ghostty.Tests pins the ordinals and
// struct layouts at build time; that test file also carries the grep
// command for re-verifying against include/ghostty.h after a rebase.

// Subset of ghostty_action_tag_e that the Windows apprt dispatches on.
// Indices are pinned explicitly so an upstream reorder cannot silently
// misroute a tag to the wrong handler — any unlisted tag falls through
// to "return false" in GhosttyHost.OnAction.
internal enum GhosttyActionTag
{
    NewTab = 2,
    CloseTab = 3,
    NewSplit = 4,
    ToggleFullscreen = 7,
    ToggleQuickTerminal = 10,
    ToggleCommandPalette = 11,
    MoveTab = 14,
    GotoTab = 15,
    GotoSplit = 16,
    ResizeSplit = 18,
    EqualizeSplits = 19,
    ToggleSplitZoom = 20,
    Scrollbar = 26,
    SetTitle = 32,
    MouseShape = 36,
    MouseVisibility = 37,
    MouseOverLink = 38,
    OpenConfig = 40,
    ReloadConfig = 47,
    ConfigChange = 48,
    CloseWindow = 49,
    RingBell = 50,
    ProgressReport = 56,
    StartSearch    = 59,
    EndSearch      = 60,
    SearchTotal    = 61,
    SearchSelected = 62,
    PromptReady    = 65,
    FirstRender    = 66,
}

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

// ghostty_action_new_split_direction_e: where the new split is placed
// relative to the focused surface.
internal enum GhosttySplitDirection { Right = 0, Down = 1, Left = 2, Up = 3 }

// ghostty_action_goto_split_e: the focus-movement variants accept a
// directional sibling; Previous/Next walk tree order, the rest pick a
// spatial neighbour.
internal enum GhosttyGotoSplit { Previous = 0, Next = 1, Up = 2, Left = 3, Down = 4, Right = 5 }

// ghostty_action_resize_split_direction_e. Note this is a *different*
// ordering from GhosttySplitDirection — resize grows the split toward
// the named edge, so the values do not line up with placement.
internal enum GhosttyResizeSplitDirection { Up = 0, Down = 1, Left = 2, Right = 3 }

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
//   { int32 state; int8 progress; /* -1 if none, else 0..100 */ }
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyActionProgressReport
{
    public int State;
    public sbyte Progress;
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
