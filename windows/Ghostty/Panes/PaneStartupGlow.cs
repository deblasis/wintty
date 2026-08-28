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
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionRadialGradientBrush _coreBrush;
    private readonly CompositionRadialGradientBrush _haloBrush;
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
        _coreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.0f, lead));
        _coreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.40f, trail));
        _coreBrush.ColorStops.Add(_compositor.CreateColorGradientStop(
            1.0f, Windows.UI.Color.FromArgb(70, trail.R, trail.G, trail.B)));

        // Halo: wide, soft, low-alpha accent bloom following the same head.
        _haloBrush = _compositor.CreateRadialGradientBrush();
        _haloBrush.MappingMode = CompositionMappingMode.Relative;
        _haloBrush.EllipseRadius = new Vector2(0.7f, 0.7f);
        _haloBrush.EllipseCenter = new Vector2(0f, 0f);
        _haloBrush.ColorStops.Add(_compositor.CreateColorGradientStop(
            0.0f, Windows.UI.Color.FromArgb(150, trail.R, trail.G, trail.B)));
        _haloBrush.ColorStops.Add(_compositor.CreateColorGradientStop(
            0.6f, Windows.UI.Color.FromArgb(45, trail.R, trail.G, trail.B)));
        _haloBrush.ColorStops.Add(_compositor.CreateColorGradientStop(
            1.0f, Windows.UI.Color.FromArgb(0, trail.R, trail.G, trail.B)));

        _geometry = _compositor.CreateRoundedRectangleGeometry();
        _geometry.CornerRadius = new Vector2(CornerRadius, CornerRadius);
        ApplyGeometrySize(size);

        // Halo drawn first (under), bright core on top. Both stroke the same
        // rounded-rect path.
        var haloShape = _compositor.CreateSpriteShape(_geometry);
        haloShape.StrokeThickness = HaloThickness;
        haloShape.FillBrush = null;
        haloShape.StrokeBrush = _haloBrush;

        var coreShape = _compositor.CreateSpriteShape(_geometry);
        coreShape.StrokeThickness = CoreThickness;
        coreShape.FillBrush = null;
        coreShape.StrokeBrush = _coreBrush;

        _shapeVisual = _compositor.CreateShapeVisual();
        _shapeVisual.Size = size;
        _shapeVisual.Shapes.Add(haloShape);
        _shapeVisual.Shapes.Add(coreShape);

        ElementCompositionPreview.SetElementChildVisual(_mount, _shapeVisual);
    }

    /// <summary>Start the forever clockwise orbit (both halo and core in sync).</summary>
    public void StartGlow()
    {
        if (_disposed) return;
        var linear = _compositor.CreateLinearEasingFunction();
        var orbit = _compositor.CreateVector2KeyFrameAnimation();
        orbit.InsertKeyFrame(0.00f, new Vector2(0f, 0f), linear); // top-left
        orbit.InsertKeyFrame(0.25f, new Vector2(1f, 0f), linear); // top-right
        orbit.InsertKeyFrame(0.50f, new Vector2(1f, 1f), linear); // bottom-right
        orbit.InsertKeyFrame(0.75f, new Vector2(0f, 1f), linear); // bottom-left
        orbit.InsertKeyFrame(1.00f, new Vector2(0f, 0f), linear); // back to start
        orbit.Duration = LoopPeriod;
        orbit.IterationBehavior = AnimationIterationBehavior.Forever;
        _coreBrush.StartAnimation("EllipseCenter", orbit);
        _haloBrush.StartAnimation("EllipseCenter", orbit);
    }

    /// <summary>Fade the whole glow to transparent over <paramref name="duration"/>.
    /// Caller disposes after.</summary>
    public void BeginFadeOut(TimeSpan duration)
    {
        if (_disposed) return;
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = duration;
        _shapeVisual.StartAnimation("Opacity", fade);
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
        _coreBrush.StopAnimation("EllipseCenter");
        _haloBrush.StopAnimation("EllipseCenter");
        _shapeVisual.StopAnimation("Opacity");
        ElementCompositionPreview.SetElementChildVisual(_mount, null);
        _shapeVisual.Dispose();
        _geometry.Dispose();
        _coreBrush.Dispose();
        _haloBrush.Dispose();
    }
}
