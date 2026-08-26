namespace Ghostty.Core.Panes;

/// <summary>
/// Thicknesses, in DIPs, of the chrome drawn around a pane, plus the
/// gutter each pane reserves for it.
///
/// The chrome -- the active-pane focus stroke and the split divider --
/// is drawn as an overlay above the split tree rather than as elements
/// inside it, so it consumes no layout space of its own. On its own that
/// makes the chrome paint over live terminal cells: the pane's full rect
/// is what gets handed to libghostty, so it sizes its grid to the full
/// rect and the renderer fills it edge to edge, right up under the
/// stroke and the divider line.
///
/// <see cref="SurfaceInset"/> is the gutter every leaf keeps clear
/// around its terminal surface so the chrome lands on empty pixels
/// instead. It is uniform across all four edges and independent of which
/// pane is focused on purpose: sizing the gutter to the active pane
/// would resize the terminal grid -- and reflow the shell -- on every
/// focus change.
/// </summary>
internal static class PaneChrome
{
    /// <summary>Stroke thickness of the active-pane focus border.</summary>
    public const double ActiveBorderThickness = 1.5;

    /// <summary>Thickness of the divider line drawn between two panes.</summary>
    public const double DividerThickness = 1.0;

    /// <summary>
    /// Largest gap, in DIPs, between a leaf's rect and the tab's content
    /// rect that still counts as the two being the same rectangle.
    /// </summary>
    /// <remarks>
    /// Star-sized cells arrange onto fractional DIPs, so a leaf that does
    /// fill its tab can still miss the content rect by a rounding
    /// remainder. Half a DIP is far below anything a stroke can show and
    /// far above that remainder.
    /// </remarks>
    public const double EdgeCoincidenceTolerance = 0.5;

    /// <summary>
    /// True when a leaf's rect fills the whole tab content rect, so the
    /// tab content border is already drawing exactly the rectangle the
    /// active-pane border would.
    /// </summary>
    /// <remarks>
    /// The two frames carry the same colour and the same thickness, so
    /// stacking them on one edge does not simply stay invisible: the
    /// stroke is antialiased against the terminal on its inner side, and
    /// blending it twice makes it darker and a shade wider than the same
    /// stroke anywhere else in the window. A tab holding one pane -- or a
    /// zoomed one, where the active leaf fills the host -- has to read as
    /// a single frame, and this is what tells the caller it is about to
    /// draw the second stroke.
    ///
    /// Only the fully-coincident case. Where a split leaf shares just some
    /// of its edges with the tab, the two strokes land on identical
    /// coordinates at identical thickness and composite to the same
    /// pixels, and suppressing those edges would take the focus frame away
    /// from the panes that sit against the tab's outside.
    /// </remarks>
    public static bool LeafFillsContent(
        double leafX, double leafY, double leafWidth, double leafHeight,
        double contentWidth, double contentHeight)
    {
        // An unarranged content rect is not something a leaf can fill. The
        // caller would otherwise drop the focus frame for the frame that
        // has no size yet, leaving the pane with no chrome at all.
        if (contentWidth <= 0 || contentHeight <= 0) return false;

        return leafX <= EdgeCoincidenceTolerance
            && leafY <= EdgeCoincidenceTolerance
            && leafX + leafWidth >= contentWidth - EdgeCoincidenceTolerance
            && leafY + leafHeight >= contentHeight - EdgeCoincidenceTolerance;
    }

    /// <summary>
    /// Gutter reserved between a pane's bounds and its terminal surface.
    ///
    /// Sized to the thickest chrome that can land on a single edge (the
    /// divider is pinned to the leading leaf's trailing edge, so it draws
    /// into that leaf's gutter alone), rounded up to a whole DIP so the
    /// surface starts on a device-pixel boundary at 100% scale.
    /// Derived rather than hardcoded so bumping a thickness above cannot
    /// silently reintroduce chrome drawing over terminal content.
    /// </summary>
    public static readonly double SurfaceInset =
        Math.Ceiling(Math.Max(ActiveBorderThickness, DividerThickness));

    /// <summary>
    /// Packed ARGB to fill the gutter with: the terminal background at
    /// the effective background opacity.
    /// </summary>
    /// <remarks>
    /// The gutter falls outside the terminal surface, so nothing paints
    /// it unless we do -- it would otherwise show the bare window
    /// backdrop and read as a differently tinted frame around every
    /// pane. The opacity has to be carried through rather than filled
    /// opaque because libghostty composites its own window padding the
    /// same way, and the gutter's job is to be indistinguishable from
    /// the surface it abuts.
    /// </remarks>
    /// <param name="backgroundRgb">Packed 0xRRGGBB background colour.</param>
    /// <param name="backgroundOpacity">Effective opacity, clamped to 0..1.</param>
    public static uint GutterArgb(uint backgroundRgb, double backgroundOpacity)
    {
        var alpha = (uint)Math.Round(Math.Clamp(backgroundOpacity, 0.0, 1.0) * 255.0);
        return (alpha << 24) | (backgroundRgb & 0x00FFFFFFu);
    }
}
