using System;
using System.Numerics;
using Ghostty.Core.Tabs;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Ghostty.Tabs;

/// <summary>
/// The join gesture's ring, shared by both strips: while a dragged tab
/// rests over a neighbour, a circle over that neighbour fills, and when
/// it completes the row haloes -- the release now joins the two into a
/// group instead of sorting them.
///
/// It is placed on a strip's hit-test-invisible overlay and moved by
/// <see cref="Place"/>, which takes the target row's arranged rect in the
/// overlay's coordinates. The fill is SET from the dwell's progress on
/// every frame rather than animated on a duration of its own: the ring is
/// the only thing telling the user what the release is about to mean, and
/// a ring running its own clock could finish early or late and lie about
/// it. The arm, which is a moment rather than a progress, is where the
/// springs are spent.
///
/// It draws no text and takes no input, so it is out of the raw
/// accessibility view: an announced ring would be a control the user
/// cannot reach, and the join a screen reader hears about is the manager
/// state the commit lands, not this.
/// </summary>
internal sealed partial class TabJoinRing : Grid
{
    private readonly Border _halo;
    private readonly Ellipse _ring;
    // The dash pattern's units are multiples of the stroke thickness, not
    // pixels, so the sweep is measured in the same units the pattern is
    // written in.
    private readonly double _sweepUnits;
    private bool _armed;

    public TabJoinRing(Brush accent)
    {
        IsHitTestVisible = false;
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);

        _halo = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = accent,
            Opacity = 0,
            IsHitTestVisible = false,
        };

        double diameter = TabStripMotion.JoinRingDiameterPx;
        double stroke = TabStripMotion.JoinRingStrokePx;
        // The stroke straddles the geometry, so the drawn circle's
        // circumference is the one through the middle of the ink.
        _sweepUnits = Math.PI * diameter / stroke;
        _ring = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Stroke = accent,
            StrokeThickness = stroke,
            StrokeDashCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            // The sweep starts at the top rather than at the ellipse
            // geometry's own start point, which is off to one side.
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = -90 },
        };
        SetProgress(0);

        Children.Add(_halo);
        Children.Add(_ring);
    }

    /// <summary>
    /// Put the ring over <paramref name="target"/> -- the row's arranged
    /// rect in the overlay's coordinates -- filled to
    /// <paramref name="progress"/>, haloed once <paramref name="armed"/>.
    /// <paramref name="motion"/> is the strip's motion gate: with it off
    /// the arm is a cut, like every other spring in the strip.
    /// </summary>
    public void Place(Rect target, double progress, bool armed, bool motion)
    {
        Width = target.Width;
        Height = target.Height;
        Canvas.SetLeft(this, target.X);
        Canvas.SetTop(this, target.Y);
        SetProgress(progress);
        SetArmed(armed, motion);
    }

    /// <summary>
    /// Back to nothing drawn. The strip calls this on every path that
    /// takes the promise back, so a halo cannot outlive the dwell that
    /// earned it.
    /// </summary>
    public void Reset()
    {
        SetProgress(0);
        SetArmed(false, motion: false);
    }

    private void SetProgress(double progress)
    {
        double filled = Math.Clamp(progress, 0, 1) * _sweepUnits;
        // A zero-length dash with a round cap still paints a dot, which
        // is the honest picture of a ring that has just started; the
        // remainder is one long gap, so exactly one arc is drawn.
        _ring.StrokeDashArray = new DoubleCollection { filled, _sweepUnits * 2 };
        _ring.Opacity = progress > 0 ? 1 : 0;
    }

    private void SetArmed(bool armed, bool motion)
    {
        if (armed == _armed) return;
        _armed = armed;
        double haloOpacity = armed ? TabStripMotion.JoinHaloOpacity : 0;
        float ringScale = armed ? TabStripMotion.JoinArmRingScale : 1f;
        if (!motion)
        {
            _halo.Opacity = haloOpacity;
            SetRingScale(ringScale);
            return;
        }
        _halo.Opacity = haloOpacity;
        SpringRingScale(ringScale);
    }

    private void SetRingScale(float scale)
    {
        if (RingVisual() is not { } visual) return;
        visual.StopAnimation("Scale");
        visual.Scale = new Vector3(scale, scale, 1f);
    }

    private void SpringRingScale(float scale)
    {
        if (RingVisual() is not { } visual) return;
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.DampingRatio = TabStripMotion.JoinArmDampingRatio;
        spring.Period = TimeSpan.FromMilliseconds(TabStripMotion.JoinArmPeriodMs);
        spring.FinalValue = new Vector3(scale, scale, 1f);
        visual.StartAnimation("Scale", spring);
    }

    private Visual? RingVisual()
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(_ring);
            visual.CenterPoint = new Vector3(
                (float)TabStripMotion.JoinRingDiameterPx / 2f,
                (float)TabStripMotion.JoinRingDiameterPx / 2f, 0f);
            return visual;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            // Composition refusing here is a cut, the same refusal family
            // every geometry read in the drag guards. The ring's fill is
            // plain XAML and keeps working without it.
            return null;
        }
    }
}
