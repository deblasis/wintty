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
    // Held apart so the armed state can choose between them: a wash normally,
    // an outline in High Contrast, where a translucent fill over the row's own
    // title is the thing the mode forbids.
    private readonly Brush _haloFill;
    private readonly Brush _haloEdge;
    // The dash pattern, allocated once and mutated: SetProgress runs on every
    // frame of a live dwell.
    private readonly DoubleCollection _dash = new() { 0, 0 };

    public TabJoinRing(Brush accent)
    {
        IsHitTestVisible = false;
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);

        _haloFill = accent;
        _haloEdge = accent;
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
        // circumference is the one through the middle of the ink -- and a
        // Shape with the default Stretch insets its geometry by half the
        // stroke on every side to keep the ink inside Width x Height. The
        // mid-ink circle is therefore (diameter - stroke) across, not
        // diameter.
        //
        // Off by that much, the dash reached full wrap at 31.4 of the 34.6
        // units the sweep believed it had: the ring closed at about 91% and
        // sat closed for the last 40ms, so a release the instant the circle
        // completed still sorted. The class exists to keep the ring from
        // lying about what the release means, and this was the ring lying
        // early.
        _sweepUnits = Math.PI * (diameter - stroke) / stroke;
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
    public void Place(
        Rect target, double progress, bool armed, bool motion, bool highContrast)
    {
        Width = target.Width;
        Height = target.Height;
        Canvas.SetLeft(this, target.X);
        Canvas.SetTop(this, target.Y);
        SetProgress(progress);
        SetArmed(armed, motion, highContrast);
    }

    /// <summary>
    /// Back to nothing drawn. The strip calls this on every path that
    /// takes the promise back, so a halo cannot outlive the dwell that
    /// earned it.
    /// </summary>
    public void Reset()
    {
        SetProgress(0);
        // Disarming needs no policy: both forms of the halo go to nothing, and
        // the next arm re-reads the live High Contrast state anyway.
        SetArmed(false, motion: false, highContrast: false);
    }

    private void SetProgress(double progress)
    {
        double filled = Math.Clamp(progress, 0, 1) * _sweepUnits;
        // One collection, mutated. This runs 62 times a second for the whole
        // of a live dwell, and the same review round removed two allocations
        // per frame from the vertical strip's dwell for exactly this reason.
        //
        // The two entries are the drawn arc and one long gap, so exactly one
        // arc is painted. A zero-length dash with a round cap still paints a
        // dot, which is the honest picture of a ring that has just started --
        // and the same round cap extends the arc by half a stroke at each end,
        // so a full ring overshoots its own start by about 1% of the
        // circumference. That is deliberate: a ring that reads closed when it
        // IS closed is the artefact this gesture wants, and the alternative
        // reads open at the moment the release changes meaning.
        _dash[0] = filled;
        _dash[1] = _sweepUnits * 2;
        _ring.StrokeDashArray = _dash;
        _ring.Opacity = progress > 0 ? 1 : 0;
    }

    private void SetArmed(bool armed, bool motion, bool highContrast)
    {
        // The halo's FORM is re-applied every call, above the edge guard.
        // Behind it, a High Contrast flip that happens inside a hold -- the
        // mode can be turned on at any moment, and the strip is told through
        // SetRowSeparator while the ring keeps being placed at 16ms -- would
        // find armed unchanged, return, and leave the translucent tint sitting
        // over the target row's title for the rest of the dwell. Which is the
        // state this branch exists to forbid.
        SetHaloForm(highContrast);

        if (armed == _armed) return;
        _armed = armed;

        double haloOpacity = armed ? (highContrast ? 1 : TabStripMotion.JoinHaloOpacity) : 0;
        float ringScale = armed ? TabStripMotion.JoinArmRingScale : 1f;
        _halo.Opacity = haloOpacity;
        if (!motion)
        {
            SetRingScale(ringScale);
            return;
        }
        SpringRingScale(ringScale);
    }

    /// <summary>
    /// What the armed halo is MADE of: a wash normally, an outline in High
    /// Contrast.
    ///
    /// The halo is a 30%-alpha accent fill over the target row, and the row has
    /// the tab's title in it -- a translucent tint laid over text, which is the
    /// contrast regression High Contrast exists to forbid. It was reached
    /// because the only thing consulted was the motion gate, which composes
    /// animations-off WITH High Contrast: two different questions, one asking
    /// whether the arm may spring and the other what the arm may be made of.
    ///
    /// The ink stays the accent, which is the same call
    /// <c>BoundaryStrokeBrush</c> makes one file along ("the system's HC accent
    /// carries the color"), so the two chrome affordances agree. A theme whose
    /// ground sits near the accent is the residual risk, and it is the strip's
    /// standing one rather than this gesture's.
    /// </summary>
    private void SetHaloForm(bool highContrast)
    {
        _halo.Background = highContrast ? null : _haloFill;
        _halo.BorderBrush = _haloEdge;
        _halo.BorderThickness = new Thickness(
            highContrast ? TabStripMotion.JoinRingStrokePx : 0);
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
