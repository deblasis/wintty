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
    Scrollbar = 26,
    SetTitle = 32,
    CloseWindow = 49,
    RingBell = 50,
    ProgressReport = 56,
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
