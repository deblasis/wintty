using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Shell;

/// <summary>
/// SPIKE. Not product behavior: inert unless WINTTY_TMODEL_SPIKE names a
/// log file, and meant to be deleted or replaced by the real port once it
/// has answered its questions.
///
/// The question under test is whether the layout switch can be driven by
/// ONE animated scalar -- a CompositionPropertySet holding "T", with every
/// visible property an ExpressionAnimation of it -- instead of today's four
/// clocks (two Storyboards, raw composition animations, and the UI
/// thread's landing turn). Three consumers are enough to answer it: the
/// two host opacities (the cross-fade with its leader) and the ghost's
/// travel. Everything else stays on the existing machinery.
///
/// What the log has to answer, each line prefixed for grepping:
///
/// - PROBE: does this SDK's expression language accept the formulas the
///   design needs -- Clamp/Pow/Lerp, a conditional, and an Exp intrinsic
///   or its Pow spelling for the damped-sine impact.
/// - SAMPLE: whether an animated property read from the UI thread
///   (the property set's own scalar, and a Visual.Opacity an expression
///   is driving) returns the sampled value or a stale one. The state
///   oracle's design depends on the answer.
/// - HANDOFF: the ownership order at the landing -- what a stopped
///   expression's property holds, and whether a XAML Opacity write after
///   it reaches the visual (the stomp question).
/// - LIVE/BATCH: whether anything outlives the switch. The spring
///   experiment failed exactly here: an unbounded animation meant the
///   scoped batch never completed and cleanup never ran, accumulating
///   across a session. T's driver is finite by construction, so the batch
///   MUST complete and the live count MUST return to zero every switch;
///   the log makes that a measurement instead of an assumption. The
///   expressions themselves are NOT self-terminating -- an
///   ExpressionAnimation runs until stopped, finite T or not -- so what is
///   actually being verified is that a finite driver gives cleanup a
///   turn that reliably arrives.
/// </summary>
internal sealed class LayoutTransitionSpike
{
    private static readonly string? TracePath =
        Environment.GetEnvironmentVariable("WINTTY_TMODEL_SPIKE");

    public static LayoutTransitionSpike? CreateIfEnabled()
        => TracePath is null ? null : new LayoutTransitionSpike();

    private CompositionPropertySet? _props;
    private ScalarKeyFrameAnimation? _driver;

    /// <summary>Everything StartAnimation was called on, for the stop.</summary>
    private readonly List<(CompositionObject Target, string Property)> _running = new();

    private Visual? _incomingVisual;
    private Visual? _outgoingVisual;
    private FrameworkElement? _incoming;
    private FrameworkElement? _outgoing;
    private EventHandler<object>? _sampler;
    private Stopwatch? _clock;
    private double _lastSampledIn = -1;

    /// <summary>
    /// Session-wide starts minus stops. The spring's failure mode was this
    /// number growing by one per switch; the design's claim is that it
    /// returns to zero at every landing.
    /// </summary>
    private static int _liveAnimations;

    private static bool _probed;

    private static void Trace(string message)
    {
        if (TracePath is null) return;
        try
        {
            System.IO.File.AppendAllText(TracePath, message + Environment.NewLine);
        }
        catch
        {
            // A locked log must never take the switch down.
        }
    }

    /// <summary>
    /// One-time probes of the expression language itself, run against a
    /// scratch property set so a refusal cannot mark the real visuals.
    /// StartAnimation is where an invalid expression surfaces, not the
    /// ExpressionAnimation constructor, so each formula is actually
    /// started.
    /// </summary>
    private static void ProbeExpressionLanguage(Compositor compositor)
    {
        if (_probed) return;
        _probed = true;

        // Two scratch targets, because StartAnimation type-checks the
        // expression's result against the property it drives: a Vector3
        // formula started on a scalar fails with the same ArgumentException
        // a genuinely invalid formula does, and the first run of this probe
        // mistook exactly that for a missing Lerp.
        var scratch = compositor.CreatePropertySet();
        scratch.InsertScalar("V", 0f);
        scratch.InsertVector3("V3", System.Numerics.Vector3.Zero);
        var refSet = compositor.CreatePropertySet();
        refSet.InsertScalar("T", 0.5f);

        void Probe(string name, string formula, bool vector = false)
        {
            var property = vector ? "V3" : "V";
            try
            {
                var expr = compositor.CreateExpressionAnimation(formula);
                expr.SetReferenceParameter("P", refSet);
                scratch.StartAnimation(property, expr);
                scratch.StopAnimation(property);
                Trace($"PROBE {name} ok: {formula}");
            }
            catch (Exception ex)
            {
                Trace($"PROBE {name} FAIL ({ex.GetType().Name} 0x{ex.HResult:x8}): {formula}");
            }
        }

        Probe("clamp-pow", "1 - Pow(1 - Clamp((P.T - 0.12) / 0.88, 0, 1), 3)");
        Probe("conditional", "P.T > 0.78 ? 1.0 : 0.0");
        Probe("exp-intrinsic", "Exp(-3.0 * P.T)");
        Probe("exp-as-pow", "Pow(2.71828, -3.0 * P.T)");
        Probe("damped-sine",
            "P.T > 0.78 ? 3.0 * Pow(2.71828, -6.0 * (P.T - 0.78)) * Sin(25.0 * (P.T - 0.78)) : 0.0");
        Probe("lerp-vector3", "Lerp(Vector3(0,0,0), Vector3(10,20,0), Clamp(P.T, 0, 1))", vector: true);
    }

    /// <summary>
    /// Take over the cross-fade for one switch. Returns false when a
    /// previous switch's spike state is somehow still live, in which case
    /// the caller keeps the storyboard fades and the run is logged as
    /// refused -- a spike must fail toward the shipping behavior.
    /// </summary>
    public bool TryStartSwitch(
        FrameworkElement incoming, FrameworkElement outgoing, TimeSpan duration)
    {
        if (_props is not null)
        {
            Trace("START refused: previous switch still live");
            return false;
        }
        try
        {
            _incoming = incoming;
            _outgoing = outgoing;
            _incomingVisual = ElementCompositionPreview.GetElementVisual(incoming);
            _outgoingVisual = ElementCompositionPreview.GetElementVisual(outgoing);
            var compositor = _incomingVisual.Compositor;

            ProbeExpressionLanguage(compositor);

            _props = compositor.CreatePropertySet();
            _props.InsertScalar("T", 0f);

            // The same curves the storyboard fades describe, as functions
            // of T. Incoming: delayed ramp, ease-out cubic. Outgoing:
            // gone by 0.6, dropping hardest first. The leader margin the
            // filmstrip asserts lives in these two formulas now.
            var fadeIn = compositor.CreateExpressionAnimation(
                "1 - Pow(1 - Clamp((P.T - 0.12) / 0.88, 0, 1), 3)");
            fadeIn.SetReferenceParameter("P", _props);
            var fadeOut = compositor.CreateExpressionAnimation(
                $"{outgoing.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + " * Pow(1 - Clamp(P.T / 0.6, 0, 1), 3)");
            fadeOut.SetReferenceParameter("P", _props);

            Start(_incomingVisual, nameof(Visual.Opacity), fadeIn);
            Start(_outgoingVisual, nameof(Visual.Opacity), fadeOut);

            // The one clock. Linear on purpose: shaping lives in the
            // consumers, so the driver stays a straight line any consumer
            // can be correlated against.
            _driver = compositor.CreateScalarKeyFrameAnimation();
            _driver.InsertKeyFrame(1f, 1f, compositor.CreateLinearEasingFunction());
            _driver.Duration = duration;

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            var clock = Stopwatch.StartNew();
            batch.Completed += (_, _) =>
            {
                // The finite driver is what makes this turn reliable; the
                // spring never got here. Logged with its lateness so the
                // report can say HOW reliable.
                Trace($"BATCH completed at {clock.ElapsedMilliseconds}ms (driver {duration.TotalMilliseconds}ms)");
            };
            Start(_props, "T", _driver);
            batch.End();

            _clock = clock;
            _lastSampledIn = -1;
            _sampler = (_, _) => Sample();
            CompositionTarget.Rendering += _sampler;

            Trace($"START in={incoming.GetType().Name} out={outgoing.GetType().Name} live={_liveAnimations}");
            return true;
        }
        catch (Exception ex)
        {
            Trace($"START FAIL ({ex.GetType().Name} 0x{ex.HResult:x8})");
            Release();
            return false;
        }
    }

    /// <summary>
    /// Take over the ghost's travel. Only the immediately-staged flight:
    /// the deferred path keeps the storyboard, because a spike earns
    /// nothing by re-solving late realization. The XAML TranslateTransform
    /// keeps holding the FROM position; the expression contributes the
    /// remaining travel through the additive hand-in Translation, so the
    /// two compose instead of fighting.
    /// </summary>
    public bool TryAttachGhost(
        FrameworkElement ghost, Windows.Foundation.Rect from, Windows.Foundation.Rect to,
        TimeSpan duration, TimeSpan switchDuration, double travelFraction)
    {
        if (_props is null) return false;
        if (duration != switchDuration)
        {
            Trace("GHOST deferred: left on the storyboard");
            return false;
        }
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(ghost, true);
            var visual = ElementCompositionPreview.GetElementVisual(ghost);
            var compositor = visual.Compositor;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var dx = (to.X - from.X).ToString("F2", inv);
            var dy = (to.Y - from.Y).ToString("F2", inv);
            var tf = travelFraction.ToString("F2", inv);
            var travel = compositor.CreateExpressionAnimation(
                $"Lerp(Vector3(0,0,0), Vector3({dx},{dy},0), 1 - Pow(1 - Clamp(P.T / {tf}, 0, 1), 3))");
            travel.SetReferenceParameter("P", _props);
            Start(visual, "Translation", travel);
            Trace($"GHOST expression travel d=({dx},{dy})");
            return true;
        }
        catch (Exception ex)
        {
            Trace($"GHOST FAIL ({ex.GetType().Name} 0x{ex.HResult:x8})");
            return false;
        }
    }

    private void Start(CompositionObject target, string property, CompositionAnimation animation)
    {
        target.StartAnimation(property, animation);
        _running.Add((target, property));
        _liveAnimations++;
    }

    /// <summary>
    /// The Q3 witness: read the driven values from the UI thread every
    /// rendered frame. If the reads are sampled, T climbs and the two
    /// opacities move with it; if they are stale, they sit at their
    /// insertion values while the screen animates. Non-monotonic incoming
    /// opacity would additionally be a mid-flight stomp caught in the act.
    /// </summary>
    private void Sample()
    {
        if (_props is null || _clock is null) return;
        float t = float.NaN;
        var status = _props.TryGetScalar("T", out var tv);
        if (status == CompositionGetValueStatus.Succeeded) t = tv;
        var vin = _incomingVisual?.Opacity ?? float.NaN;
        var vout = _outgoingVisual?.Opacity ?? float.NaN;
        var stomp = vin < _lastSampledIn - 0.01 ? " STOMP" : "";
        _lastSampledIn = vin;
        Trace($"SAMPLE t={_clock.ElapsedMilliseconds} T={t:F3}({status}) in={vin:F3} out={vout:F3}{stomp}");
    }

    /// <summary>
    /// The landing half of the experiment, run from FinishSwitch BEFORE
    /// Snap: stop everything and log what the stopped properties hold.
    /// Snap then writes the element opacities, and <see cref="ProbeAfterSnap"/>
    /// reads the visuals again to see whether those writes arrived.
    /// </summary>
    public void StopExpressions()
    {
        if (_props is null) return;
        if (_sampler is not null)
        {
            CompositionTarget.Rendering -= _sampler;
            _sampler = null;
        }
        var inBefore = _incomingVisual?.Opacity ?? float.NaN;
        var outBefore = _outgoingVisual?.Opacity ?? float.NaN;
        foreach (var (target, property) in _running)
        {
            try { target.StopAnimation(property); } catch { }
            _liveAnimations--;
        }
        _running.Clear();
        var inHeld = _incomingVisual?.Opacity ?? float.NaN;
        var outHeld = _outgoingVisual?.Opacity ?? float.NaN;
        Trace($"HANDOFF stop: in {inBefore:F3}->{inHeld:F3} out {outBefore:F3}->{outHeld:F3} live={_liveAnimations}");
    }

    /// <summary>
    /// Run after Snap has written the element opacities: whether the XAML
    /// writes reached the visuals a stopped expression was holding.
    ///
    /// Same-value comparisons cannot answer that -- at the landing the
    /// stopped expression holds the exact value Snap writes, which is the
    /// design's landing invariant working as intended but tells nothing
    /// about ownership. So a DIFFERENTIAL write: put a value nothing else
    /// uses on the element, read the visual, put the real value back. Both
    /// writes happen inside one turn, so nothing of it can render. The
    /// synchronous reads say whether writes propagate immediately; the one
    /// extra rendered-frame read distinguishes commit-lagged from never.
    /// </summary>
    public void ProbeAfterSnap()
    {
        if (_incomingVisual is null) return;
        Trace($"HANDOFF snap: visual in={_incomingVisual.Opacity:F3} out={_outgoingVisual?.Opacity ?? float.NaN:F3}"
            + $" element in={_incoming?.Opacity ?? double.NaN:F2} out={_outgoing?.Opacity ?? double.NaN:F2}");

        if (_incoming is { } element && _incomingVisual is { } visual)
        {
            var restore = element.Opacity;
            element.Opacity = 0.5;
            var mid = visual.Opacity;
            element.Opacity = restore;
            var back = visual.Opacity;
            Trace($"HANDOFF differential: wrote 0.5 -> visual {mid:F3}; wrote {restore:F2} -> visual {back:F3}");

            // One rendered frame later, against locals rather than spike
            // state, which Release is about to drop.
            var clock = _clock;
            EventHandler<object>? once = null;
            once = (_, _) =>
            {
                CompositionTarget.Rendering -= once;
                Trace($"HANDOFF nextframe: visual in={visual.Opacity:F3} element in={element.Opacity:F2}"
                    + (clock is null ? "" : $" t={clock.ElapsedMilliseconds}"));
            };
            CompositionTarget.Rendering += once;
        }
        Release();
    }

    /// <summary>
    /// Idempotent teardown, also the cancel path: stop whatever is still
    /// running and drop the state. A spike may not leave animations on a
    /// closing window any more than the product may.
    /// </summary>
    public void Release()
    {
        if (_sampler is not null)
        {
            CompositionTarget.Rendering -= _sampler;
            _sampler = null;
        }
        foreach (var (target, property) in _running)
        {
            try { target.StopAnimation(property); } catch { }
            _liveAnimations--;
        }
        _running.Clear();
        if (_props is not null)
            Trace($"RELEASE live={_liveAnimations}");
        _props = null;
        _driver = null;
        _incomingVisual = null;
        _outgoingVisual = null;
        _incoming = null;
        _outgoing = null;
        _clock = null;
    }
}
