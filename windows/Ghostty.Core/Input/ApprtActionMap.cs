using Ghostty.Core.Interop;

namespace Ghostty.Core.Input;

/// <summary>
/// Translates a libghostty apprt action (tag + decoded value) into the
/// Windows apprt's <see cref="PaneAction"/>. Pure so it is unit-tested
/// without any native or UI machinery. Returns null for tags this apprt
/// does not act on, or values outside the supported range.
/// </summary>
// Internal rather than public: Map consumes GhosttyActionTag and the
// directional enums, which are internal FFI mirrors. Ghostty.Tests reaches
// it through InternalsVisibleTo.
internal static class ApprtActionMap
{
    public static PaneAction? Map(GhosttyActionTag tag, int value) => tag switch
    {
        GhosttyActionTag.NewTab => PaneAction.NewTab,
        GhosttyActionTag.CloseTab => PaneAction.CloseActiveProgressive,
        GhosttyActionTag.ToggleFullscreen => PaneAction.ToggleFullscreen,
        GhosttyActionTag.EqualizeSplits => PaneAction.EqualizeSplits,
        GhosttyActionTag.ToggleSplitZoom => PaneAction.ToggleSplitZoom,

        GhosttyActionTag.NewSplit => (GhosttySplitDirection)value switch
        {
            GhosttySplitDirection.Right => PaneAction.SplitVertical,
            GhosttySplitDirection.Down => PaneAction.SplitHorizontal,
            _ => null, // left/up not represented in the current PaneAction set
        },

        GhosttyActionTag.GotoSplit => (GhosttyGotoSplit)value switch
        {
            GhosttyGotoSplit.Previous => PaneAction.GotoSplitPrevious,
            GhosttyGotoSplit.Next => PaneAction.GotoSplitNext,
            GhosttyGotoSplit.Up => PaneAction.FocusUp,
            GhosttyGotoSplit.Down => PaneAction.FocusDown,
            GhosttyGotoSplit.Left => PaneAction.FocusLeft,
            GhosttyGotoSplit.Right => PaneAction.FocusRight,
            _ => null,
        },

        GhosttyActionTag.ResizeSplit => (GhosttyResizeSplitDirection)value switch
        {
            GhosttyResizeSplitDirection.Up => PaneAction.ResizeSplitUp,
            GhosttyResizeSplitDirection.Down => PaneAction.ResizeSplitDown,
            GhosttyResizeSplitDirection.Left => PaneAction.ResizeSplitLeft,
            GhosttyResizeSplitDirection.Right => PaneAction.ResizeSplitRight,
            _ => null,
        },

        GhosttyActionTag.GotoTab => value switch
        {
            -1 => PaneAction.PrevTab,
            -2 => PaneAction.NextTab,
            -3 => PaneAction.JumpTabLast,
            >= 1 and <= 8 => PaneAction.JumpTab1 + (value - 1),
            _ => null,
        },

        GhosttyActionTag.MoveTab => value switch
        {
            > 0 => PaneAction.MoveTabRight,
            < 0 => PaneAction.MoveTabLeft,
            _ => null,
        },

        GhosttyActionTag.GotoWindow => (GhosttyGotoWindow)value switch
        {
            GhosttyGotoWindow.Previous => PaneAction.GotoWindowPrevious,
            GhosttyGotoWindow.Next => PaneAction.GotoWindowNext,
            _ => null,
        },

        GhosttyActionTag.FloatWindow => (GhosttyFloatWindow)value switch
        {
            GhosttyFloatWindow.On => PaneAction.FloatWindowOn,
            GhosttyFloatWindow.Off => PaneAction.FloatWindowOff,
            GhosttyFloatWindow.Toggle => PaneAction.FloatWindowToggle,
            _ => null,
        },

        // Payload-free window actions: value is unused.
        GhosttyActionTag.ResetWindowSize => PaneAction.ResetWindowSize,
        GhosttyActionTag.ToggleBackgroundOpacity => PaneAction.ToggleBackgroundOpacity,

        _ => null,
    };
}
