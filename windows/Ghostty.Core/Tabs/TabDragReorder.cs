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
/// State machine for the vertical strip's drag-to-reorder gesture: the
/// press threshold, commit-on-center-crossing with hysteresis, the
/// autoscroll ramp, release velocity, and the terminal drop/cancel
/// transitions. Pure data in, decisions out, so the whole gesture
/// grammar is unit-testable without a WinUI host.
///
/// The strip owns every measurement and all composition, and feeds
/// results in here. Row centers are ARRANGED positions in strip space
/// (never raw pointer positions, so scroll cannot corrupt a crossing),
/// and a crossing is reported, not applied -- the strip turns it into
/// <see cref="TabManager.Move"/>, keeping the manager the truth
/// mid-drag.
/// </summary>
public sealed class TabDragReorder
{
    private readonly double _startThresholdPx;
    private readonly double _hysteresisPx;
    // Arranged row centers by manager index. The strip re-feeds these
    // from layout; between a commit and the next measurement this class
    // keeps them true by swapping the two rows it just reordered, so a
    // fast flick can chain crossings on stale layout.
    private double[] _centers;
    private int _index;
    private TabDragPhase _phase;
    private double _pressY;
    // Trailing window of pointer samples release velocity reads from.
    private readonly List<(double Ms, double Y)> _samples = new();

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
    public void Press(double pointerY)
    {
        if (_phase != TabDragPhase.Idle) return;
        _pressY = pointerY;
        _phase = TabDragPhase.Pressed;
    }

    /// <summary>
    /// Lift to <see cref="TabDragPhase.Dragging"/> once vertical travel
    /// passes the start threshold. Horizontal movement never starts the
    /// drag, so a jittering grab stays a click.
    /// </summary>
    public bool Begin(double pointerY)
    {
        if (_phase != TabDragPhase.Pressed) return false;
        if (Math.Abs(pointerY - _pressY) < _startThresholdPx) return false;
        _phase = TabDragPhase.Dragging;
        return true;
    }

    /// <summary>
    /// Re-feed arranged row centers after layout or scroll. Centers the
    /// strip has not measured yet stay as the swaps left them.
    /// </summary>
    public void UpdateCenters(IReadOnlyList<double> centers)
    {
        if (centers.Count != _centers.Length)
            _centers = new double[centers.Count];
        for (int i = 0; i < centers.Count; i++) _centers[i] = centers[i];
        if (_index >= _centers.Length) _index = _centers.Length - 1;
    }

    /// <summary>
    /// Re-derive the dragged row's index after a membership change mid-drag
    /// (a tab opened or closed elsewhere): the index is manager truth.
    /// </summary>
    public void UpdateIndex(int index)
    {
        if (index < 0 || index >= _centers.Length) return;
        _index = index;
    }

    /// <summary>Arranged center the machine currently believes row <c>index</c> has.</summary>
    public double CenterOf(int index)
        => index >= 0 && index < _centers.Length ? _centers[index] : 0;

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
    public TabDragCrossing? Evaluate(double draggedCenterY)
    {
        if (_phase != TabDragPhase.Dragging) return null;

        if (_index + 1 < _centers.Length
            && draggedCenterY > _centers[_index + 1] + _hysteresisPx)
        {
            _index++;
            return new TabDragCrossing(_index - 1, _index);
        }

        if (_index > 0 && draggedCenterY < _centers[_index - 1] - _hysteresisPx)
        {
            _index--;
            return new TabDragCrossing(_index + 1, _index);
        }

        return null;
    }

    /// <summary>
    /// Signed autoscroll speed in px/s at <paramref name="pointerY"/>
    /// against the scrolling viewport's bounds: negative scrolls up,
    /// positive down, zero outside the edge band. Ramps with distance so
    /// the ramp-up is proportional to how far into the band the drag is.
    /// </summary>
    public double AutoscrollSpeed(double pointerY, double viewportTop, double viewportBottom)
    {
        double fromTop = pointerY - viewportTop;
        double fromBottom = viewportBottom - pointerY;
        double d = Math.Min(fromTop, fromBottom);
        if (d > TabStripMotion.AutoscrollBandPx) return 0;

        double speed = d <= TabStripMotion.AutoscrollInnerBandPx
            ? TabStripMotion.AutoscrollMaxPxPerSecond
            : TabStripMotion.AutoscrollBasePxPerSecond
              + (TabStripMotion.AutoscrollMaxPxPerSecond - TabStripMotion.AutoscrollBasePxPerSecond)
              * (TabStripMotion.AutoscrollBandPx - d)
              / (TabStripMotion.AutoscrollBandPx - TabStripMotion.AutoscrollInnerBandPx);

        return fromTop <= fromBottom ? -speed : speed;
    }

    /// <summary>Feed the velocity window. <paramref name="ms"/> is monotonic.</summary>
    public void SampleVelocity(double y, double ms)
    {
        _samples.RemoveAll(s => s.Ms < ms - TabStripMotion.VelocityWindowMs);
        _samples.Add((ms, y));
    }

    /// <summary>
    /// Pointer release velocity over the trailing window, clamped so a
    /// fling cannot overshoot the slot: the cap is the remaining
    /// distance per settle period. A release whose trailing motion runs
    /// AWAY from the slot carries no settle velocity at all -- the
    /// spring owns the direction, and handing it a fling against the
    /// travel would throw the row the wrong way before reeling it back.
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
    /// drag, and reports -1.
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
