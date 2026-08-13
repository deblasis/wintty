using System;

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
public static class PaneChrome
{
    /// <summary>Stroke thickness of the active-pane focus border.</summary>
    public const double ActiveBorderThickness = 1.5;

    /// <summary>Thickness of the divider line drawn between two panes.</summary>
    public const double DividerThickness = 1.0;

    /// <summary>
    /// Gutter reserved between a pane's bounds and its terminal surface.
    ///
    /// Sized to the thickest chrome that can land on a single edge (the
    /// divider rides the boundary between two leaves, so it draws into
    /// one gutter or the other, never both), rounded up to a whole DIP so
    /// the surface starts on a device-pixel boundary at 100% scale.
    /// Derived rather than hardcoded so bumping a thickness above cannot
    /// silently reintroduce chrome drawing over terminal content.
    /// </summary>
    public static readonly double SurfaceInset =
        Math.Ceiling(Math.Max(ActiveBorderThickness, DividerThickness));

    /// <summary>
    /// The terminal surface extent along one axis for a pane of the given
    /// extent, with the gutter removed from both edges.
    /// </summary>
    /// <remarks>
    /// Clamped at zero: a pane briefly narrower than its two gutters
    /// (mid-drag, or a split of an already tiny pane) would otherwise
    /// yield a negative extent, which wraps to an enormous value once
    /// cast to the unsigned pixel size libghostty takes.
    /// </remarks>
    public static double SurfaceExtent(double paneExtent)
        => Math.Max(0.0, paneExtent - SurfaceInset * 2);

    /// <summary>
    /// Packed ARGB to fill the gutter with: the terminal background at
    /// the effective background opacity.
    /// </summary>
    /// <remarks>
    /// The gutter falls outside the terminal surface, so nothing paints
    /// it unless we do -- it would otherwise show the bare window
    /// backdrop and read as a differently tinted frame around every
    /// pane. Carrying the opacity through (rather than filling opaque)
    /// is what makes it match: libghostty composites its own window
    /// padding the same way, so at any opacity the gutter and the
    /// padding just inside it resolve to the same pixel.
    /// </remarks>
    /// <param name="backgroundRgb">Packed 0xRRGGBB background colour.</param>
    /// <param name="backgroundOpacity">Effective opacity, clamped to 0..1.</param>
    public static uint GutterArgb(uint backgroundRgb, double backgroundOpacity)
    {
        var alpha = (uint)Math.Round(Math.Clamp(backgroundOpacity, 0.0, 1.0) * 255.0);
        return (alpha << 24) | (backgroundRgb & 0x00FFFFFFu);
    }
}
