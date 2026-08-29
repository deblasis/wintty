using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Ghostty.Panes;

/// <summary>
/// Composition renderer for the pane startup glow: a rounded-rectangle border
/// painted by a radial gradient whose center orbits the pane clockwise, so a
/// soft bright segment sweeps the inner border. Driven imperatively by PaneHost
/// (StartGlow / BeginFadeOut / Dispose) off the pure state machine.
///
/// Two stacked strokes give a glow that reads on ANY background (not just the
/// dimmed inactive-pane film): a wide, soft, low-alpha <c>halo</c> for bloom,
/// and a thin bright <c>core</c> for the sharp sweeping head. The whole ring
/// also carries a faint base tint so it is visible the instant a pane opens.
///
/// Tuning constants (thickness, radius, stops, loop period) live here; the
/// lifecycle timing (cap, fade) lives in PaneStartupGlowState.
/// </summary>
internal sealed class PaneStartupGlow : IDisposable
{
    private const float CoreThickness = 4f;
    private const float HaloThickness = 12f;
    private const float CornerRadius = 8f;
    private static readonly TimeSpan LoopPeriod = TimeSpan.FromMilliseconds(1400);

    private readonly FrameworkElement _mount;
    private readonly Compositor _compositor;
    private readonly ShapeVisual _shapeVisual;
    private readonly SpriteShape _coreShape;
    private readonly SpriteShape _haloShape;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionColorGradientStopCollection _coreStops;
    private readonly CompositionColorGradientStopCollection _haloStops;
    private readonly CompositionRadialGradientBrush _coreBrush;
    private readonly CompositionRadialGradientBrush _haloBrush;
    private readonly ScalarKeyFrameAnimation _fade;
    private readonly Vector2KeyFrameAnimation _orbit;
    private readonly LinearEasingFunction _easing;
    private bool _disposed;

    /// <param name="mount">An empty FrameworkElement (sized/positioned by
    /// PaneHost over the leaf) that hosts this glow's child visual.</param>
    /// <param name="size">Initial mount size in DIPs.</param>
    /// <param name="trail">Trailing color (accent/cursor), passed fully opaque;
    /// the gradient stops apply their own alpha for the halo/tail.</param>
    /// <param name="lead">Leading-edge color (foreground), passed fully opaque;
    /// the gradient stops apply their own alpha for the halo/tail.</param>
    public PaneStartupGlow(FrameworkElement mount, Vector2 size,
        Windows.UI.Color trail, Windows.UI.Color lead)
    {
        _mount = mount;
        _compositor = ElementCompositionPreview.GetElementVisual(mount).Compositor;

        // Core: bright leading head -> accent body -> faint accent base. The
        // base alpha (not zero) keeps the whole ring softly lit so the glow is
        // visible immediately, even over a bright focused-pane background.
        _coreBrush = _compositor.CreateRadialGradientBrush();
        _coreBrush.MappingMode = CompositionMappingMode.Relative;
        _coreBrush.EllipseRadius = new Vector2(0.5f, 0.5f);
        _coreBrush.EllipseCenter = new Vector2(0f, 0f);
        _coreStops = _coreBrush.ColorStops;
        _coreStops.Add(_compositor.CreateColorGradientStop(0.0f, lead));
        _coreStops.Add(_compositor.CreateColorGradientStop(0.40f, trail));
        _coreStops.Add(_compositor.CreateColorGradientStop(
            1.0f, Windows.UI.Color.FromArgb(70, trail.R, trail.G, trail.B)));

        // Halo: wide, soft, low-alpha accent bloom following the same head.
        _haloBrush = _compositor.CreateRadialGradientBrush();
        _haloBrush.MappingMode = CompositionMappingMode.Relative;
        _haloBrush.EllipseRadius = new Vector2(0.7f, 0.7f);
        _haloBrush.EllipseCenter = new Vector2(0f, 0f);
        _haloStops = _haloBrush.ColorStops;
        _haloStops.Add(_compositor.CreateColorGradientStop(
            0.0f, Windows.UI.Color.FromArgb(150, trail.R, trail.G, trail.B)));
        _haloStops.Add(_compositor.CreateColorGradientStop(
            0.6f, Windows.UI.Color.FromArgb(45, trail.R, trail.G, trail.B)));
        _haloStops.Add(_compositor.CreateColorGradientStop(
            1.0f, Windows.UI.Color.FromArgb(0, trail.R, trail.G, trail.B)));

        _geometry = _compositor.CreateRoundedRectangleGeometry();
        _geometry.CornerRadius = new Vector2(CornerRadius, CornerRadius);
        ApplyGeometrySize(size);

        // Halo drawn first (under), bright core on top. Both stroke the same
        // rounded-rect path.
        _haloShape = _compositor.CreateSpriteShape(_geometry);
        _haloShape.StrokeThickness = HaloThickness;
        _haloShape.FillBrush = null;
        _haloShape.StrokeBrush = _haloBrush;

        _coreShape = _compositor.CreateSpriteShape(_geometry);
        _coreShape.StrokeThickness = CoreThickness;
        _coreShape.FillBrush = null;
        _coreShape.StrokeBrush = _coreBrush;

        _shapeVisual = _compositor.CreateShapeVisual();
        _shapeVisual.Size = size;
        _shapeVisual.Shapes.Add(_haloShape);
        _shapeVisual.Shapes.Add(_coreShape);

        // Animations are built here rather than at start/fade time so that
        // Dispose owns every composition object this class creates. Nothing
        // is lost: the orbit's key frames are fixed (four corners, clockwise),
        // the fade's target is fixed (transparent), and only the two
        // durations wait for their caller.
        _easing = _compositor.CreateLinearEasingFunction();
        _orbit = _compositor.CreateVector2KeyFrameAnimation();
        _orbit.InsertKeyFrame(0.00f, new Vector2(0f, 0f), _easing); // top-left
        _orbit.InsertKeyFrame(0.25f, new Vector2(1f, 0f), _easing); // top-right
        _orbit.InsertKeyFrame(0.50f, new Vector2(1f, 1f), _easing); // bottom-right
        _orbit.InsertKeyFrame(0.75f, new Vector2(0f, 1f), _easing); // bottom-left
        _orbit.InsertKeyFrame(1.00f, new Vector2(0f, 0f), _easing); // back to start
        _orbit.Duration = LoopPeriod;
        _orbit.IterationBehavior = AnimationIterationBehavior.Forever;
        _fade = _compositor.CreateScalarKeyFrameAnimation();
        _fade.InsertKeyFrame(1f, 0f);

        ElementCompositionPreview.SetElementChildVisual(_mount, _shapeVisual);
    }

    /// <summary>Start the forever clockwise orbit (both halo and core in sync).</summary>
    public void StartGlow()
    {
        if (_disposed) return;
        _coreBrush.StartAnimation("EllipseCenter", _orbit);
        _haloBrush.StartAnimation("EllipseCenter", _orbit);
    }

    /// <summary>Fade the whole glow to transparent over <paramref name="duration"/>.
    /// Caller disposes after.</summary>
    public void BeginFadeOut(TimeSpan duration)
    {
        if (_disposed) return;
        _fade.Duration = duration;
        _shapeVisual.StartAnimation("Opacity", _fade);
    }

    /// <summary>Resize the glow when the leaf bounds change.</summary>
    public void UpdateSize(Vector2 size)
    {
        if (_disposed) return;
        _shapeVisual.Size = size;
        ApplyGeometrySize(size);
    }

    private void ApplyGeometrySize(Vector2 size)
    {
        // Inset the geometry by half the WIDEST stroke (the halo) so the whole
        // band stays inside the leaf bounds and nothing gets clipped by the
        // shape visual's size.
        var half = HaloThickness / 2f;
        _geometry.Offset = new Vector2(half, half);
        _geometry.Size = new Vector2(
            Math.Max(0f, size.X - HaloThickness),
            Math.Max(0f, size.Y - HaloThickness));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Animations stop before the objects they drive, then owners go
        // before their dependents: the visual that holds both shapes, the
        // shapes, and the geometry they both stroke.
        _coreBrush.StopAnimation("EllipseCenter");
        _haloBrush.StopAnimation("EllipseCenter");
        _shapeVisual.StopAnimation("Opacity");
        ElementCompositionPreview.SetElementChildVisual(_mount, null);

        _shapeVisual.Dispose();
        _coreShape.Dispose();
        _haloShape.Dispose();
        _geometry.Dispose();

        // Each brush's gradient graph. The stops are composition objects of
        // their own and go before the collection holding them: enumerating a
        // closed collection is the one order that would throw.
        DisposeStops(_coreStops);
        _coreStops.Dispose();
        _coreBrush.Dispose();
        DisposeStops(_haloStops);
        _haloStops.Dispose();
        _haloBrush.Dispose();

        // The animations themselves, the forever orbit included: left running
        // it keeps the compositor animating the property of a brush nobody
        // paints with, for the rest of the window's life.
        _fade.Dispose();
        _orbit.Dispose();
        _easing.Dispose();
    }

    private static void DisposeStops(CompositionColorGradientStopCollection stops)
    {
        foreach (var stop in stops)
            stop.Dispose();
    }
}
