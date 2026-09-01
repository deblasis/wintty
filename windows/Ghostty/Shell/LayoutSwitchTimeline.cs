using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Ghostty.Shell;

/// <summary>
/// One layout switch's animations, as functions of one clock.
///
/// The switch used to run on four clocks -- two Storyboards, raw
/// composition animations with their own delays, and the UI thread's
/// landing turn -- and the seams between them were measured as the stutter
/// the owner reported: an accent scheduled by predicted delay started
/// 90-150ms after the motion it punctuated, and the landing arrived as its
/// own discrete repaint. This type replaces scheduling with construction.
/// It owns a <see cref="CompositionPropertySet"/> holding two scalars, and
/// every visible property of the switch is an <see cref="ExpressionAnimation"/>
/// over them:
///
/// - <c>T</c> runs 0 to 1 over the switch duration. Fades, slides, the
///   ghost's travel and box, the label, the icon spin and the pane reveal
///   are all pure functions of T, so at any frame they agree with each
///   other by arithmetic rather than by four clocks happening to line up.
/// - <c>S</c> runs 0 to 1 over the impact's span, delayed until the
///   switch has visibly finished plus a short lead-out. The impact is a
///   function of S. Both drivers start in the same commit, so their
///   relative timing is fixed by the compositor rather than predicted
///   across commits -- which is what the old delay got wrong by 90-150ms.
///
/// Cleanup is explicit, and that is a measured lesson rather than a
/// preference: expressions are NOT self-terminating -- an
/// ExpressionAnimation runs until stopped, finite driver or not -- and an
/// earlier spring experiment leaked exactly here (no finite duration, so
/// its scoped batch never completed and its cleanup never ran,
/// accumulating across a session). The finite drivers are what make the
/// two cleanup turns provably arrive: the T driver's scoped batch lands
/// the switch, the S driver's releases the tail. Only the drivers sit
/// inside their batches -- an expression started inside a batch scope
/// never completes, and the batch would wait on it forever.
///
/// The landing invariant, which is what makes the UI thread's lateness
/// invisible instead of pretending the thread can be punctual: at T = 1
/// every switch-phase expression evaluates to exactly the value the
/// landing writes. <see cref="CompleteSwitchPhase"/> then stops each
/// expression and writes that value through to the visual's client-side
/// property -- written explicitly rather than trusted to coincide,
/// because the spike showed client-side reads and writes tell nothing
/// about the server-side value an expression held, and correctness must
/// not depend on the two agreeing by accident.
/// </summary>
internal sealed class LayoutSwitchTimeline
{
    /// <summary>
    /// One animated property this timeline started, with the client-side
    /// write that lands its end value. Stop-then-write, in that order:
    /// a value write while the animation still runs updates only the base
    /// value underneath it.
    /// </summary>
    private readonly record struct Entry(
        CompositionObject Target, string Property, Action WriteEnd);

    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _props;
    private readonly TimeSpan _switchDuration;

    // Split by which cleanup turn owns them: switch-phase entries are
    // stopped and written at the landing, tail-phase entries (anything
    // carrying the impact term) at the tail batch, because stopping a
    // combined slide+impact expression at the landing would freeze the
    // accent mid-shove.
    private readonly List<Entry> _switchEntries = new();
    private readonly List<Entry> _tailEntries = new();

    /// <summary>
    /// A beat between the motion ending and the accent landing: enough
    /// for the switch to visibly finish, short enough that the accent
    /// still belongs to it. About three frames at 60Hz.
    /// </summary>
    public static readonly TimeSpan ImpactLeadOut = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// How long the impact takes. Close to what the original three-step
    /// window nudge actually measured (~140ms), so the feel is the one
    /// that shipped.
    /// </summary>
    public static readonly TimeSpan ImpactDuration = TimeSpan.FromMilliseconds(140);

    /// <summary>How far the struck strip is pushed, at the peak.</summary>
    private const double ImpactPeakPixels = 4.0;

    // The impact's shape: one damped oscillation, amp * e^(-decay*S) *
    // sin(2*pi*S). Peak at S=0.25 (about 35ms in -- the push is quick and
    // the settle is the longer half), one small opposite lobe (~0.5px)
    // standing in for the old easeOutBack's overshoot past rest, and
    // exactly zero at S=1 because sin(2*pi) is. Decay 4 sizes that second
    // lobe; the amplitude is solved so the first peak is ImpactPeakPixels:
    // amp = peak / (e^(-decay*0.25) * sin(pi/2)) = peak * e.
    private const double ImpactDecay = 4.0;
    private static readonly double ImpactAmp = ImpactPeakPixels * Math.E;

    public LayoutSwitchTimeline(Compositor compositor, TimeSpan switchDuration)
    {
        _compositor = compositor;
        _switchDuration = switchDuration;
        _props = compositor.CreatePropertySet();
        _props.InsertScalar("T", 0f);
        _props.InsertScalar("S", 0f);
        // The impact term is authored into the incoming strip's expression
        // up front (its direction is known at Animate) but contributes
        // nothing until a ghost flight actually stages and arms it: a
        // switch whose morph never staged keeps its accent, like the old
        // pending-delay that was never set.
        _props.InsertScalar("Armed", 0f);
    }

    /// <summary>
    /// Authored progress of the switch phase, for staging that arrives
    /// mid-flight: the deferred ghost spends only what is left of the
    /// switch, so its expressions need to know where T already is. The
    /// wall clock is the authority the drivers were started against, so
    /// it is what late staging reads -- the animated scalar itself reads
    /// stale from the UI thread (measured; see the state oracle notes in
    /// the filmstrip harness).
    /// </summary>
    public double Progress =>
        _clock is { } clock
            ? Math.Clamp(clock.Elapsed.TotalMilliseconds / _switchDuration.TotalMilliseconds, 0, 1)
            : 0;

    private System.Diagnostics.Stopwatch? _clock;

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    // Easing vocabulary, as expression fragments over an argument already
    // clamped to [0,1]. Kept here, once, so every consumer of a curve is
    // the same algebra rather than a re-derivation that can drift.
    private static string EaseOutCubic(string u) => $"(1 - Pow(1 - {u}, 3))";
    private static string EaseInCubic(string u) => $"Pow({u}, 3)";
    private static string EaseInOutCubic(string u) =>
        $"(({u} < 0.5) ? (4 * Pow({u}, 3)) : (1 - Pow(-2 * {u} + 2, 3) / 2))";

    private static string ClampT(string offsetSpan) => $"Clamp(P.T{offsetSpan}, 0, 1)";

    private ExpressionAnimation Expr(string formula)
    {
        var e = _compositor.CreateExpressionAnimation(formula);
        e.SetReferenceParameter("P", _props);
        return e;
    }

    private void Register(
        List<Entry> phase, CompositionObject target, string property,
        ExpressionAnimation animation, Action writeEnd)
    {
        target.StartAnimation(property, animation);
        phase.Add(new Entry(target, property, writeEnd));
    }

    private static Visual VisualOf(FrameworkElement element)
        => ElementCompositionPreview.GetElementVisual(element);

    private static Visual TranslationVisualOf(FrameworkElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        return ElementCompositionPreview.GetElementVisual(element);
    }

    /// <summary>
    /// The cross-fade's arriving half: held at zero for the leader delay,
    /// then an eased ramp to full. The delay and the outgoing end below
    /// are the leader margin the filmstrip's oracle used to watch live;
    /// see the curve-margin test for where that property is asserted now.
    /// </summary>
    public void FadeIn(FrameworkElement element, double delayFraction)
    {
        var u = $"Clamp((P.T - {F(delayFraction)}) / {F(1 - delayFraction)}, 0, 1)";
        Register(
            _switchEntries, VisualOf(element), nameof(Visual.Opacity),
            Expr(EaseOutCubic(u)),
            () => VisualOf(element).Opacity = 1f);
    }

    /// <summary>
    /// The departing half: committed to leaving in the opening frames
    /// (it drops hardest first) and gone by <paramref name="endFraction"/>.
    /// </summary>
    public void FadeOut(FrameworkElement element, double fromOpacity, double endFraction)
    {
        var u = $"Clamp(P.T / {F(endFraction)}, 0, 1)";
        Register(
            _switchEntries, VisualOf(element), nameof(Visual.Opacity),
            Expr($"{F(fromOpacity)} * Pow(1 - {u}, 3)"),
            () => VisualOf(element).Opacity = 0f);
    }

    /// <summary>
    /// The incoming strip's emerge slide, with the impact term authored
    /// in: Translation = slide(T) + Armed * direction * shape(S). One
    /// expression per property is load-bearing -- a separate impact
    /// animation on the same Translation would simply replace the slide,
    /// because a property carries one animation at a time.
    ///
    /// Tail-phase: the slide term is zero from T=1 on, but the impact
    /// term is still to come, so this entry outlives the landing and is
    /// released by the tail batch instead.
    /// </summary>
    public void SlideInWithImpact(
        FrameworkElement element, Windows.Foundation.Point from,
        double impactDx, double impactDy)
    {
        var slide = $"Lerp(Vector3({F(from.X)}, {F(from.Y)}, 0), Vector3(0, 0, 0), {EaseOutCubic(ClampT(""))})";
        Register(
            _tailEntries, TranslationVisualOf(element), "Translation",
            Expr($"{slide} + {ImpactTerm(impactDx, impactDy)}"),
            () => TranslationVisualOf(element).Properties
                .InsertVector3("Translation", System.Numerics.Vector3.Zero));
    }

    /// <summary>
    /// The impact alone, for the morph overlay: the ghost is standing on
    /// the incoming strip's tab slot when the accent runs, and it lives on
    /// a canvas outside the strip, so the overlay rides the same shove or
    /// the ghost shears against the row it covers.
    /// </summary>
    public void ImpactOnly(FrameworkElement element, double impactDx, double impactDy)
    {
        Register(
            _tailEntries, TranslationVisualOf(element), "Translation",
            Expr(ImpactTerm(impactDx, impactDy)),
            () => TranslationVisualOf(element).Properties
                .InsertVector3("Translation", System.Numerics.Vector3.Zero));
    }

    private static string ImpactTerm(double dx, double dy) =>
        $"(P.Armed * {F(ImpactAmp)} * Pow(2.71828183, -{F(ImpactDecay)} * P.S)"
        + $" * Sin(6.28318531 * P.S)) * Vector3({F(dx)}, {F(dy)}, 0)";

    /// <summary>
    /// Arm the impact term. Called where the ghost's flight stages, which
    /// is the one place that knows the accent has something to punctuate.
    /// A plain property write: the expressions read it server-side, so an
    /// arm that lands mid-flight takes effect without restarting anything.
    /// </summary>
    public void ArmImpact() => _props.InsertScalar("Armed", 1f);

    public void SlideOut(FrameworkElement element, Windows.Foundation.Point to)
    {
        var slide = $"Lerp(Vector3(0, 0, 0), Vector3({F(to.X)}, {F(to.Y)}, 0), {EaseInCubic(ClampT(""))})";
        Register(
            _switchEntries, TranslationVisualOf(element), "Translation",
            Expr(slide),
            // Rest, not the slide's own end: the element is collapsed and
            // fully transparent by the time this is written, and the next
            // switch expects to find it where layout put it.
            () => TranslationVisualOf(element).Properties
                .InsertVector3("Translation", System.Numerics.Vector3.Zero));
    }

    /// <summary>
    /// Hold an icon still inside a sliding host: the exact negation of
    /// the incoming slide, the same algebra with the sign flipped, so the
    /// cancellation is by construction rather than by two curves agreeing.
    /// Deliberately NOT a reference to the host's animated Translation --
    /// that would cancel the impact shove too, and the icon should ride
    /// the shove with the strip that carries it.
    /// </summary>
    public void CounterSlideIn(FrameworkElement element, Windows.Foundation.Point hostFrom)
    {
        var slide = $"Lerp(Vector3({F(-hostFrom.X)}, {F(-hostFrom.Y)}, 0), Vector3(0, 0, 0), {EaseOutCubic(ClampT(""))})";
        Register(
            _switchEntries, TranslationVisualOf(element), "Translation",
            Expr(slide),
            () => TranslationVisualOf(element).Properties
                .InsertVector3("Translation", System.Numerics.Vector3.Zero));
    }

    public void CounterSlideOut(FrameworkElement element, Windows.Foundation.Point hostTo)
    {
        var slide = $"Lerp(Vector3(0, 0, 0), Vector3({F(-hostTo.X)}, {F(-hostTo.Y)}, 0), {EaseInCubic(ClampT(""))})";
        Register(
            _switchEntries, TranslationVisualOf(element), "Translation",
            Expr(slide),
            () => TranslationVisualOf(element).Properties
                .InsertVector3("Translation", System.Numerics.Vector3.Zero));
    }

    // The icon spin's settle overshoot, spelled as XAML's BackEase does it
    // (f(t) = t^3 - t*a*sin(pi*t), applied ease-out) so the feel survives
    // the port: the icon's shove-and-settle was tuned against that exact
    // curve.
    private static string BackEaseOut(string u, double amplitude) =>
        $"(1 - (Pow(1 - {u}, 3) - (1 - {u}) * {F(amplitude)} * Sin(3.14159265 * (1 - {u}))))";

    /// <summary>
    /// Spin and pop one icon in place. The rotation pivots on the visual's
    /// own centre, tracked by expression against its size rather than set
    /// from a measurement that may not have happened yet.
    /// </summary>
    public void SpinIcon(
        FrameworkElement element, double degrees,
        double spinOvershoot, double popMidpoint, double popDipScale, double popOvershoot)
    {
        var visual = VisualOf(element);
        visual.StartAnimation(
            nameof(Visual.CenterPoint),
            Expr("Vector3(this.Target.Size.X / 2, this.Target.Size.Y / 2, 0)"));
        // The centre expression is not registered: it is not a motion, it
        // is the pivot definition, and it must survive until the rotation
        // is released. Stopped alongside the rotation below.
        var rot = Expr($"{F(degrees)} * {BackEaseOut(ClampT(""), spinOvershoot)}");
        Register(
            _switchEntries, visual, nameof(Visual.RotationAngleInDegrees), rot,
            () =>
            {
                visual.StopAnimation(nameof(Visual.CenterPoint));
                visual.RotationAngleInDegrees = 0f;
            });

        // Dip partway through, then spring back past full inside the same
        // segment -- the pop the old key frames described, as one curve.
        var mid = F(popMidpoint);
        var dipLeg = $"Lerp(1.0, {F(popDipScale)}, (1 - Pow(1 - Clamp(P.T / {mid}, 0, 1), 2)))";
        var popLeg =
            $"Lerp({F(popDipScale)}, 1.0, {BackEaseOut($"Clamp((P.T - {mid}) / {F(1 - popMidpoint)}, 0, 1)", popOvershoot)})";
        var scale = Expr($"(P.T < {mid}) ? Vector3({dipLeg}, {dipLeg}, 1) : Vector3({popLeg}, {popLeg}, 1)");
        Register(
            _switchEntries, visual, nameof(Visual.Scale), scale,
            () => visual.Scale = System.Numerics.Vector3.One);
    }

    /// <summary>
    /// The ghost's travel: an additive delta on top of the XAML translate
    /// that holds its departure point. <paramref name="startT"/> is where
    /// the switch already is when staging happens -- zero for a flight
    /// staged up front, later for one that waited out container
    /// realization -- and the travel spends only the window between it and
    /// the travel-settle fraction, so a deferred ghost still lands with
    /// the cross-fade rather than after it.
    /// </summary>
    public void GhostTravel(
        FrameworkElement ghost, double deltaX, double deltaY,
        double startT, double travelFraction)
    {
        var span = (1 - startT) * travelFraction;
        var u = $"Clamp((P.T - {F(startT)}) / {F(span)}, 0, 1)";
        Register(
            _switchEntries, TranslationVisualOf(ghost), "Translation",
            Expr($"Lerp(Vector3(0, 0, 0), Vector3({F(deltaX)}, {F(deltaY)}, 0), {EaseOutCubic(u)})"),
            () => TranslationVisualOf(ghost).Properties
                .InsertVector3("Translation", new System.Numerics.Vector3((float)deltaX, (float)deltaY, 0)));
    }

    /// <summary>
    /// The ghost's box: the fill scaled between the two rects about its
    /// top-left, the content clipped to the same box rather than scaled
    /// into it. Same shape-settle window for all three so the box is one
    /// motion. The ease matches the pane reveal's, so the ghost's edge and
    /// the terminal's edge read as one motion rather than two that happen
    /// to overlap.
    /// </summary>
    public void GhostBox(
        Visual body, InsetClip contentClip,
        System.Numerics.Vector2 fromScale, System.Numerics.Vector2 toScale,
        float rightFrom, float rightTo, float bottomFrom, float bottomTo,
        double startT, double shapeFraction)
    {
        var span = (1 - startT) * shapeFraction;
        var u = $"Clamp((P.T - {F(startT)}) / {F(span)}, 0, 1)";
        var e = EaseInOutCubic(u);

        Register(
            _switchEntries, body, nameof(Visual.Scale),
            Expr($"Lerp(Vector3({F(fromScale.X)}, {F(fromScale.Y)}, 1), Vector3({F(toScale.X)}, {F(toScale.Y)}, 1), {e})"),
            () => body.Scale = new System.Numerics.Vector3(toScale, 1f));

        // An inset that never moves is set, not animated: a formula from a
        // value to itself is work the compositor carries all flight to no
        // effect.
        if (Math.Abs(rightFrom - rightTo) >= 0.5f)
        {
            Register(
                _switchEntries, contentClip, nameof(InsetClip.RightInset),
                Expr($"Lerp({F(rightFrom)}, {F(rightTo)}, {e})"),
                () => contentClip.RightInset = rightTo);
        }
        else
        {
            contentClip.RightInset = rightTo;
        }

        if (Math.Abs(bottomFrom - bottomTo) >= 0.5f)
        {
            Register(
                _switchEntries, contentClip, nameof(InsetClip.BottomInset),
                Expr($"Lerp({F(bottomFrom)}, {F(bottomTo)}, {e})"),
                () => contentClip.BottomInset = bottomTo);
        }
        else
        {
            contentClip.BottomInset = bottomTo;
        }
    }

    /// <summary>
    /// The ghost label's legibility fade, on the leg that loses or gains
    /// the room for it. It leaves early on the same kind of curve the
    /// outgoing strip does: a fully legible tab crossing the terminal
    /// reads as something being dragged; a shape with its text gone reads
    /// as chrome rearranging itself, which is what is happening.
    /// </summary>
    public void GhostLabelFade(
        UIElement label, bool shrinking, double startT, double labelFraction)
    {
        var span = (1 - startT) * labelFraction;
        var u = $"Clamp((P.T - {F(startT)}) / {F(span)}, 0, 1)";
        var visual = ElementCompositionPreview.GetElementVisual(label);
        var formula = shrinking
            ? $"1 - {EaseOutCubic(u)}"
            : EaseOutCubic(u);
        Register(
            _switchEntries, visual, nameof(Visual.Opacity),
            Expr(formula),
            () => visual.Opacity = shrinking ? 0f : 1f);
    }

    /// <summary>
    /// The pane reveal's sweep: the terminal was resized once, up front,
    /// and hidden under this inset; the edge glides open at the
    /// compositor's rate whatever the UI thread is doing.
    /// </summary>
    public void PaneRevealSweep(InsetClip clip, double laneWidth)
    {
        Register(
            _switchEntries, clip, nameof(InsetClip.LeftInset),
            Expr($"{F(laneWidth)} * (1 - {EaseInOutCubic(ClampT(""))})"),
            () => clip.LeftInset = 0f);
    }

    /// <summary>
    /// Start the two drivers, in one commit. Everything visible is
    /// already expressed over the scalars they drive, so this is the one
    /// instant the whole switch is clocked from.
    ///
    /// Only a driver sits inside its batch: the expressions were started
    /// before this method, deliberately, because an expression inside a
    /// batch scope never completes and the batch would never fire. The
    /// landing callback is raised on the UI thread when the T driver
    /// finishes -- late by whatever the thread is doing, which is fine,
    /// because at T = 1 the screen already shows the exact end state and
    /// the landing turn only does bookkeeping the eye cannot see.
    /// </summary>
    public void Begin(Action landed)
    {
        var linear = _compositor.CreateLinearEasingFunction();

        var tDriver = _compositor.CreateScalarKeyFrameAnimation();
        tDriver.InsertKeyFrame(1f, 1f, linear);
        tDriver.Duration = _switchDuration;
        var landing = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        landing.Completed += (_, _) => landed();
        _props.StartAnimation("T", tDriver);
        landing.End();

        var sDriver = _compositor.CreateScalarKeyFrameAnimation();
        sDriver.InsertKeyFrame(0f, 0f);
        sDriver.InsertKeyFrame(1f, 1f, linear);
        sDriver.Duration = ImpactDuration;
        sDriver.DelayTime = _switchDuration + ImpactLeadOut;
        // Hold zero through the delay rather than jumping to the first
        // key frame when the animation is handed over.
        sDriver.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var tail = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        // The tail's cleanup needs no identity check: it stops only what
        // this timeline itself still holds, and a preempting switch has
        // already drained these entries by the time it could collide.
        tail.Completed += (_, _) => ReleaseEntries(_tailEntries, writeEndValues: true);
        _props.StartAnimation("S", sDriver);
        tail.End();

        _clock = System.Diagnostics.Stopwatch.StartNew();
    }

    /// <summary>
    /// The landing, per the invariant: stop every switch-phase expression
    /// and write its end value through to the client-side property, so
    /// the value the screen holds and the value later code reads or
    /// writes over are the same one on purpose. Runs before Snap, which
    /// then writes the element-level end state over ground that already
    /// agrees with it.
    /// </summary>
    public void CompleteSwitchPhase() => ReleaseEntries(_switchEntries, writeEndValues: true);

    /// <summary>
    /// Stop everything this timeline still has running. With
    /// <paramref name="writeEndValues"/> the visuals land on their end
    /// state (a preempting switch about to re-measure the tree); without
    /// it they are merely released (a closing window, where the values
    /// will never be seen and the writes are work for nobody).
    /// </summary>
    public void Release(bool writeEndValues)
    {
        ReleaseEntries(_switchEntries, writeEndValues);
        ReleaseEntries(_tailEntries, writeEndValues);
    }

    private static void ReleaseEntries(List<Entry> entries, bool writeEndValues)
    {
        foreach (var entry in entries)
        {
            try
            {
                entry.Target.StopAnimation(entry.Property);
                if (writeEndValues) entry.WriteEnd();
            }
            catch
            {
                // The visual is already gone, which for the closing-window
                // caller is the outcome this was reaching for.
            }
        }
        entries.Clear();
    }
}


