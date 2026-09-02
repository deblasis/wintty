using System;
using System.Collections.Generic;

namespace Ghostty.Core.Tabs;

/// <summary>The vertical drag's phase, for the strip's handlers to gate on.</summary>
public enum TabDragPhase
{
    /// <summary>No gesture. The machine is reusable after it returns here.</summary>
    Idle,

    /// <summary>Pointer is down on a row, under the start threshold: still a click.</summary>
    Pressed,

    /// <summary>Drag is live: the row follows the pointer, crossings commit.</summary>
    Dragging,
}

/// <summary>One committed reorder: the dragged row moves <c>From</c> to <c>To</c>.</summary>
public readonly record struct TabDragCrossing(int From, int To);

/// <summary>
/// State machine for a strip's drag-to-reorder gesture, either axis:
/// the press threshold, commit-on-center-crossing with hysteresis, the
/// autoscroll ramp, release velocity, and the terminal drop/cancel
/// transitions. Pure data in, decisions out, so the whole gesture
/// grammar is unit-testable without a WinUI host.
///
/// The strip owns every measurement and all composition, and feeds
/// results in here. Every position is a scalar ALONG THE AXIS -- the
/// vertical strip feeds Y, the horizontal strip feeds X -- and row
/// centers are ARRANGED positions in strip space (never raw pointer
/// positions, so scroll cannot corrupt a crossing), and a crossing is
/// reported, not applied -- the strip turns it into
/// <see cref="TabManager.Move"/>, keeping the manager the truth
/// mid-drag.
/// </summary>
public sealed class TabDragReorder
{
    private readonly double _startThresholdPx;
    private readonly double _hysteresisPx;
    // Arranged row centers as a function of slot: entry i is slot i's
    // center for whichever row sits at manager index i. Commits leave
    // them untouched -- rows occupy slots in manager order, so a reorder
    // needs no surgery here -- and the only reason to re-feed is a change
    // in row metrics: scroll, resize, membership. A re-feed must carry
    // FINAL arranged positions, never in-flight glide positions; a
    // center read off a row still gliding poisons the crossing
    // thresholds and the anchor alike.
    private double[] _centers;
    private int _index;
    private TabDragPhase _phase;
    private double _pressPosition;
    // Trailing window of pointer samples release velocity reads from.
    private readonly List<(double Ms, double Position)> _samples = new();

    public TabDragReorder(int rowCount, int grabIndex)
        : this(rowCount, grabIndex,
              TabStripMotion.GrabStartThresholdPx, TabStripMotion.CrossingHysteresisPx) { }

    public TabDragReorder(int rowCount, int grabIndex, double startThresholdPx, double hysteresisPx)
    {
        if (rowCount < 2)
            throw new ArgumentException(
                "TabDragReorder needs at least two rows; there is nothing to reorder.");
        if (grabIndex < 0 || grabIndex >= rowCount)
            throw new ArgumentOutOfRangeException(nameof(grabIndex));
        _startThresholdPx = startThresholdPx;
        _hysteresisPx = hysteresisPx;
        _centers = new double[rowCount];
        _index = grabIndex;
    }

    public TabDragPhase Phase => _phase;

    /// <summary>Current manager index of the dragged row.</summary>
    public int Index => _index;

    /// <summary>Arm the gesture on a pointer press over row <c>grabIndex</c>.</summary>
    public void Press(double position)
    {
        if (_phase != TabDragPhase.Idle) return;
        _pressPosition = position;
        _phase = TabDragPhase.Pressed;
    }

    /// <summary>
    /// Lift to <see cref="TabDragPhase.Dragging"/> once travel along the
    /// axis passes the start threshold. Movement along the OTHER axis
    /// never starts the drag, so a jittering grab stays a click.
    /// </summary>
    public bool Begin(double position)
        => BeginOnTravel(Math.Abs(position - _pressPosition));

    /// <summary>
    /// Lift on a travel the CALLER measured, for a grab whose gesture is
    /// not confined to this machine's axis.
    ///
    /// The pinned band is the one such surface: it wraps, so two of its
    /// squares can share this machine's axis exactly, and a reorder between
    /// them is pure cross-axis travel. Asked through
    /// <see cref="Begin(double)"/> that gesture can never lift at all --
    /// the row the user is dragging sideways sits at an unchanging Y, and
    /// the press stays a click forever.
    ///
    /// The one-axis rule stays the DEFAULT, and deliberately: a body row is
    /// dragged up and down a list, and admitting sideways travel there
    /// would turn a hand tremor on a click into a drag. Only a caller that
    /// knows its surface has a second axis is allowed to say so, and it
    /// says so by measuring the travel itself.
    /// </summary>
    public bool BeginOnTravel(double travelPx)
    {
        if (_phase != TabDragPhase.Pressed) return false;
        if (travelPx < _startThresholdPx) return false;
        _phase = TabDragPhase.Dragging;
        return true;
    }

    /// <summary>
    /// Re-feed arranged row centers. Centers are a function of slot and
    /// survive commits untouched, so this is only needed when row metrics
    /// change -- scroll, resize, membership -- and it must carry FINAL
    /// arranged positions: a center read off a row still mid-glide
    /// poisons both the crossing thresholds and the anchor. Slots the
    /// strip cannot measure yet keep their previous value; a shrunken
    /// strip clamps the dragged index.
    /// </summary>
    public void UpdateCenters(IReadOnlyList<double> centers)
    {
        if (centers.Count != _centers.Length)
            _centers = new double[centers.Count];
        for (int i = 0; i < centers.Count; i++) _centers[i] = centers[i];
        if (_index >= _centers.Length) _index = _centers.Length - 1;
    }

    /// <summary>
    /// Re-derive the dragged row's index after a membership change
    /// elsewhere (a tab opened or closed): the index is manager truth.
    /// Asymmetric with <see cref="UpdateCenters"/> by design: an
    /// out-of-range index is dropped rather than clamped, because the
    /// only way the dragged row's own index can leave range is the row
    /// closing -- and a mid-drag close is the STRIP's job to answer with
    /// Cancel, not an index update the machine is expected to absorb.
    /// </summary>
    public void UpdateIndex(int index)
    {
        if (index < 0 || index >= _centers.Length) return;
        _index = index;
    }

    /// <summary>
    /// Arranged center the machine currently believes the row at
    /// <c>index</c> has. Throws for any index outside the strip: asking
    /// about a row the machine does not know is a strip bug, and
    /// laundering it as 0 would bend a crossing past the first slot.
    /// Bounds come from <see cref="RowCount"/>.
    /// </summary>
    public double CenterOf(int index)
    {
        if (index < 0 || index >= _centers.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _centers[index];
    }

    /// <summary>How many rows the machine currently holds centers for.</summary>
    public int RowCount => _centers.Length;

    /// <summary>
    /// Commit-on-center-crossing: the dragged row's center against its
    /// neighbours' arranged centers, plus the hysteresis guard, so a
    /// crossing only commits once the drag clearly means it and a
    /// backtrack must re-earn the previous slot.
    ///
    /// Returns one crossing per call; the strip applies it through the
    /// manager and calls again until null, so a fast flick across three
    /// rows is three ordinary moves, not one rewrite. The centers are
    /// NOT rewritten here: they are indexed by manager order, and rows
    /// occupy slots in manager order once layout catches up, so a
    /// committed crossing leaves them describing exactly the layout the
    /// strip is arranging toward. The backtrack hysteresis therefore
    /// measures against the neighbour's new slot, and undoing a swap
    /// costs a full row of travel, not the 8px that committed it.
    /// </summary>
    public TabDragCrossing? Evaluate(double draggedCenter)
    {
        if (_phase != TabDragPhase.Dragging) return null;

        if (_index + 1 < _centers.Length
            && draggedCenter > _centers[_index + 1] + _hysteresisPx)
        {
            _index++;
            return new TabDragCrossing(_index - 1, _index);
        }

        if (_index > 0 && draggedCenter < _centers[_index - 1] - _hysteresisPx)
        {
            _index--;
            return new TabDragCrossing(_index + 1, _index);
        }

        return null;
    }

    /// <summary>
    /// Signed autoscroll speed in px/s at <paramref name="position"/>
    /// against the scrolling viewport's bounds along the axis: negative
    /// scrolls toward the start, positive toward the end, zero outside
    /// the edge band. Ramps with distance so the ramp-up is proportional
    /// to how far into the band the drag is.
    /// </summary>
    public double AutoscrollSpeed(double position, double viewportStart, double viewportEnd)
    {
        double fromStart = position - viewportStart;
        double fromEnd = viewportEnd - position;
        double d = Math.Min(fromStart, fromEnd);
        if (d > TabStripMotion.AutoscrollBandPx) return 0;

        double speed = d <= TabStripMotion.AutoscrollInnerBandPx
            ? TabStripMotion.AutoscrollMaxPxPerSecond
            : TabStripMotion.AutoscrollBasePxPerSecond
              + (TabStripMotion.AutoscrollMaxPxPerSecond - TabStripMotion.AutoscrollBasePxPerSecond)
              * (TabStripMotion.AutoscrollBandPx - d)
              / (TabStripMotion.AutoscrollBandPx - TabStripMotion.AutoscrollInnerBandPx);

        return fromStart <= fromEnd ? -speed : speed;
    }

    /// <summary>Feed the velocity window. <paramref name="ms"/> is monotonic.</summary>
    public void SampleVelocity(double position, double ms)
    {
        // Front-prune rather than RemoveAll: this runs per pointer move,
        // and a lambda there allocates a closure on every call.
        double cutoff = ms - TabStripMotion.VelocityWindowMs;
        int stale = 0;
        while (stale < _samples.Count && _samples[stale].Ms < cutoff) stale++;
        if (stale > 0) _samples.RemoveRange(0, stale);
        _samples.Add((ms, position));
    }

    /// <summary>
    /// Pointer release velocity over the trailing window, clamped so a
    /// fling cannot overshoot the slot: the cap is the remaining
    /// distance per settle period. A release whose trailing motion runs
    /// AWAY from the slot carries no settle velocity at all -- the
    /// spring owns the direction, and handing it a fling against the
    /// travel would throw the row the wrong way before reeling it back.
    ///
    /// <paramref name="remainingDistancePx"/> is signed on the machine's
    /// axis, positive = toward the axis end; passing a bare magnitude
    /// makes every velocity read as running away from the slot and this
    /// guard kills legitimate settles against the travel.
    ///
    /// Read BEFORE <see cref="Drop"/> or <see cref="Cancel"/>: both clear
    /// the sample window, so the natural call order reports 0 forever
    /// with every test still green.
    /// </summary>
    public double ReleaseVelocity(double remainingDistancePx)
    {
        if (_samples.Count < 2) return 0;
        var (firstMs, firstY) = _samples[0];
        var (lastMs, lastY) = _samples[^1];
        double dtSec = (lastMs - firstMs) / 1000.0;
        if (dtSec <= 0) return 0;
        double v = (lastY - firstY) / dtSec;
        if (v * remainingDistancePx <= 0) return 0;
        double cap = Math.Abs(remainingDistancePx) / (TabStripMotion.SettlePeriodMs / 1000.0);
        return Math.Clamp(v, -cap, cap);
    }

    /// <summary>
    /// Terminal: pointer released over a live drag. Returns the dragged
    /// row's committed index; the manager already holds it. A release
    /// that never lifted past the start threshold was a click, not a
    /// drag, and reports -1. Clears the velocity window, so read
    /// <see cref="ReleaseVelocity"/> first.
    /// </summary>
    public int Drop()
    {
        if (_phase != TabDragPhase.Dragging)
        {
            Reset();
            return -1;
        }
        int index = _index;
        Reset();
        return index;
    }

    /// <summary>Terminal: the gesture ends without a drop (escape, capture loss, teardown).</summary>
    public void Cancel() => Reset();

    private void Reset()
    {
        _phase = TabDragPhase.Idle;
        _samples.Clear();
    }
}
