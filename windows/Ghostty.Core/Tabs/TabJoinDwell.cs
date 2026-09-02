using System;

namespace Ghostty.Core.Tabs;

/// <summary>
/// The join gesture's clock: while a tab is dragged, holding it still
/// over a neighbour fills a ring, and a release once the ring is full
/// JOINS the two into a group instead of sorting them. A release before
/// it fills is the ordinary reorder the crossings already committed.
///
/// The dwell is short -- <see cref="TabStripMotion.JoinDwellMs"/>, not
/// the couple of seconds a hold-to-confirm usually costs -- because the
/// ring is showing the whole time: the wait only has to outlast a hand
/// passing through, not carry the discovery on its own.
///
/// The clock is an ARGUMENT, never read in here. Two reasons, both
/// load-bearing. The strips have to advance the ring with no pointer
/// events at all (a hand held perfectly still raises none), so something
/// outside has to say what time it is; and a 450ms hold asserted against
/// a wall clock on a loaded thread pool is a flake, so the test seam
/// hands this a clock it owns and the hold becomes a fact the test
/// states rather than a race it hopes to win.
///
/// Targets are matched by REFERENCE, so this class never learns what a
/// tab is. The strips pass their own row models and read the target back
/// out at the release.
/// </summary>
internal sealed class TabJoinDwell
{
    private readonly double _dwellMs;
    private readonly double _jitterPx;
    private object? _target;
    // Where the pointer was when the current ring started filling. The
    // ring is a promise about a pointer that has STOPPED, so travel past
    // the jitter token re-arms it from zero.
    private double _anchor;
    private long _startMs;

    public TabJoinDwell()
        : this(TabStripMotion.JoinDwellMs, TabStripMotion.JoinJitterPx) { }

    public TabJoinDwell(double dwellMs, double jitterPx)
    {
        if (dwellMs <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(dwellMs),
                "a dwell of zero would arm the ring on the frame the pointer arrived, "
                + "which is the gesture this exists to keep apart from a quick release.");
        _dwellMs = dwellMs;
        _jitterPx = jitterPx;
    }

    /// <summary>
    /// What the ring is currently drawn over, or null when no dwell is
    /// live. The release reads this: the target is the join's other half,
    /// and re-deriving it from geometry at release time would answer for
    /// wherever the hand drifted in the last frame.
    /// </summary>
    public object? Target => _target;

    /// <summary>How full the ring is, 0..1. Zero whenever nothing is targeted.</summary>
    public double Progress { get; private set; }

    /// <summary>
    /// The ring completed and the release now means JOIN. Stays true
    /// while the same target is held: once the ring is full the promise
    /// is made, and only leaving the target (or ending the gesture) takes
    /// it back. Small movements after the arm are the hand resting, not a
    /// change of mind.
    /// </summary>
    public bool IsArmed => _target is not null && Progress >= 1;

    /// <summary>
    /// Advance the dwell for one frame over <paramref name="target"/> --
    /// null meaning the drag is over nothing joinable, which withdraws
    /// the ring. <paramref name="position"/> is the pointer along the
    /// strip's axis and <paramref name="ms"/> is a monotonic clock, both
    /// the caller's. Answers <see cref="IsArmed"/>.
    /// </summary>
    public bool Hold(object? target, double position, long ms)
    {
        if (target is null)
        {
            Clear();
            return false;
        }
        if (!ReferenceEquals(target, _target))
        {
            Restart(target, position, ms);
            return false;
        }
        if (Progress < 1)
        {
            // A clock that went backwards is a seam clock re-armed
            // between gestures, never elapsed time: restarting is the
            // only honest answer, and completing the ring off a negative
            // elapsed is the alternative.
            if (Math.Abs(position - _anchor) > _jitterPx || ms < _startMs)
            {
                Restart(target, position, ms);
                return false;
            }
            Progress = Math.Clamp((ms - _startMs) / _dwellMs, 0, 1);
        }
        return Progress >= 1;
    }

    /// <summary>
    /// Withdraw the ring. Every path that ends a gesture funnels here, so
    /// an armed dwell cannot outlive the drag that armed it and be read
    /// by the next one.
    /// </summary>
    public void Clear()
    {
        _target = null;
        Progress = 0;
    }

    private void Restart(object target, double position, long ms)
    {
        _target = target;
        _anchor = position;
        _startMs = ms;
        Progress = 0;
    }
}
