namespace Ghostty.Core.Input;

/// <summary>
/// Mirror of <c>ghostty_action_mouse_shape_e</c>
/// (<c>include/ghostty.h</c>) and <c>terminal.MouseShape</c>
/// (<c>src/terminal/mouse.zig</c>). Ordinals are pinned by
/// <c>MouseShapeMapTests.Ordinal_Matches_Upstream</c>.
///
/// To re-verify against upstream after a rebase:
///   grep -nE "^  GHOSTTY_MOUSE_SHAPE_" include/ghostty.h
/// </summary>
public enum MouseShape
{
    Default = 0,
    ContextMenu = 1,
    Help = 2,
    Pointer = 3,
    Progress = 4,
    Wait = 5,
    Cell = 6,
    Crosshair = 7,
    Text = 8,
    VerticalText = 9,
    Alias = 10,
    Copy = 11,
    Move = 12,
    NoDrop = 13,
    NotAllowed = 14,
    Grab = 15,
    Grabbing = 16,
    AllScroll = 17,
    ColResize = 18,
    RowResize = 19,
    NResize = 20,
    EResize = 21,
    SResize = 22,
    WResize = 23,
    NeResize = 24,
    NwResize = 25,
    SeResize = 26,
    SwResize = 27,
    EwResize = 28,
    NsResize = 29,
    NeswResize = 30,
    NwseResize = 31,
    ZoomIn = 32,
    ZoomOut = 33,
}

/// <summary>
/// Intermediate "system cursor family" enum. The 34 libghostty shapes
/// collapse into 13 families because Windows / WinUI 3's
/// <c>InputSystemCursorShape</c> doesn't cover everything (no built-in
/// Alias, Copy, ZoomIn/Out, ContextMenu, VerticalText cursors).
/// Lossy shapes degrade to <c>Arrow</c>.
///
/// Lives in Ghostty.Core so the mapping is unit-testable without
/// dragging WinAppSDK into Ghostty.Tests.
/// </summary>
// NOTE: The explicit ordinals below are defensive pinning only —
// MouseShapeFamily is an internal abstraction with no FFI binding, so
// only the enum member NAMES are load-bearing. Reordering members
// without updating the explicit values would be a harmless rename
// from the perspective of every consumer (MouseShapeMap.ToFamily and
// MouseShapeAdapter.ToWinUI both match by name).
public enum MouseShapeFamily
{
    Arrow = 0,
    Hand = 1,
    IBeam = 2,
    Wait = 3,
    AppStarting = 4,
    Cross = 5,
    Help = 6,
    SizeAll = 7,
    UniversalNo = 8,
    SizeWestEast = 9,
    SizeNorthSouth = 10,
    SizeNortheastSouthwest = 11,
    SizeNorthwestSoutheast = 12,
}

/// <summary>
/// Pure mapping from libghostty's 34 CSS-style cursor hints to the
/// 13 system-cursor families supported by Windows. The WinUI adapter
/// in <c>Ghostty/Controls/MouseShapeAdapter.cs</c> turns these into
/// <c>InputSystemCursorShape</c> values.
/// </summary>
public static class MouseShapeMap
{
    public static MouseShapeFamily ToFamily(MouseShape shape) =>
        shape switch
        {
            // Direct 1:1 matches first.
            MouseShape.Help        => MouseShapeFamily.Help,
            MouseShape.Wait        => MouseShapeFamily.Wait,
            MouseShape.Progress    => MouseShapeFamily.AppStarting,
            // Pointer / Grab / Grabbing all mean "actionable target".
            MouseShape.Pointer     => MouseShapeFamily.Hand,
            MouseShape.Grab        => MouseShapeFamily.Hand,
            MouseShape.Grabbing    => MouseShapeFamily.Hand,
            // Text caret. Windows has no rotated I-beam, so vertical
            // text falls back to the horizontal one.
            MouseShape.Text         => MouseShapeFamily.IBeam,
            MouseShape.VerticalText => MouseShapeFamily.IBeam,
            // Crosshair + table-cell selection both = crosshair on Windows.
            MouseShape.Cell        => MouseShapeFamily.Cross,
            MouseShape.Crosshair   => MouseShapeFamily.Cross,
            // Move / all-scroll = the 4-way arrow.
            MouseShape.Move        => MouseShapeFamily.SizeAll,
            MouseShape.AllScroll   => MouseShapeFamily.SizeAll,
            // Forbidden actions.
            MouseShape.NoDrop      => MouseShapeFamily.UniversalNo,
            MouseShape.NotAllowed  => MouseShapeFamily.UniversalNo,
            // Axis resizes.
            MouseShape.ColResize   => MouseShapeFamily.SizeWestEast,
            MouseShape.EResize     => MouseShapeFamily.SizeWestEast,
            MouseShape.WResize     => MouseShapeFamily.SizeWestEast,
            MouseShape.EwResize    => MouseShapeFamily.SizeWestEast,
            MouseShape.RowResize   => MouseShapeFamily.SizeNorthSouth,
            MouseShape.NResize     => MouseShapeFamily.SizeNorthSouth,
            MouseShape.SResize     => MouseShapeFamily.SizeNorthSouth,
            MouseShape.NsResize    => MouseShapeFamily.SizeNorthSouth,
            // Diagonal resizes. NE/SW share an axis; NW/SE share the other.
            MouseShape.NeResize    => MouseShapeFamily.SizeNortheastSouthwest,
            MouseShape.SwResize    => MouseShapeFamily.SizeNortheastSouthwest,
            MouseShape.NeswResize  => MouseShapeFamily.SizeNortheastSouthwest,
            MouseShape.NwResize    => MouseShapeFamily.SizeNorthwestSoutheast,
            MouseShape.SeResize    => MouseShapeFamily.SizeNorthwestSoutheast,
            MouseShape.NwseResize  => MouseShapeFamily.SizeNorthwestSoutheast,
            // Lossy: Windows has no native Alias / Copy / ZoomIn-Out /
            // ContextMenu / Default cursors beyond Arrow. The `_`
            // catch-all also handles future libghostty additions safely.
            _                      => MouseShapeFamily.Arrow,
        };
}
