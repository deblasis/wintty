using System;
using Ghostty.Core.Panes;
using Ghostty.Motion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Panes;

/// <summary>
/// A held split-geometry transition: the layout-side capability the
/// unified split choreography (a registered motion coordinator) drives.
/// Motion-neutral on purpose - this file knows curves exist only as a
/// [0, 1] progress the OWNER samples; it never names an easing family,
/// a duration, or a policy itself.
///
/// The contract, in one paragraph: a split's commit can be HELD at its
/// start geometry (the origin pane whole in its pre-split rect, the
/// newborn's cell at zero) instead of snapping to the model ratio; the
/// owner drives the boundary to the model's final ratio by progress on
/// its own clock, one synchronous layout per drive; anything else that
/// touches the tree (a tree operation's settle, a divider drag, a
/// dropped clock past the bound) SETTLES the transition to the model
/// ratio - the layout never rests anywhere the model does not say.
/// The model ratio itself is never touched: the transient lives in the
/// split Grid's star weights alone, so a snapshot, a capture or an
/// undo taken mid-flight reads the split at its final ratio.
/// </summary>
internal sealed class SplitGeometryTransition
{
    /// <summary>The bound on a silent transition: past this many
    /// milliseconds without a drive or a settle, the transition settles
    /// itself. A healthy flight is a few hundred milliseconds; the
    /// bound exists so a clock that died (an owner that never armed,
    /// an engine that lost its thread) cannot hold a layout mid-split
    /// forever.</summary>
    internal const double SettleBoundMs = 2000;

    private readonly PaneHost _host;
    private readonly SplitPane _split;
    private readonly double _startedAtMs;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _boundTimer;
    private double _lastWritten = double.NaN;

    internal SplitGeometryTransition(PaneHost host, SplitPane split)
    {
        _host = host;
        _split = split;
        _startedAtMs = NowMs();
        _boundTimer = Microsoft.UI.Dispatching.DispatcherQueue
            .GetForCurrentThread().CreateTimer();
        _boundTimer.Interval = TimeSpan.FromMilliseconds(SettleBoundMs);
        _boundTimer.IsRepeating = false;
        _boundTimer.Tick += (_, _) => Settle("bound");
    }

    private static double NowMs()
        => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Whether the owner claimed this hold (in the coordinator's
    /// Changed report, the same UI turn the split committed in). An
    /// unclaimed hold does not survive the split's own call stack: the
    /// commit site settles it before returning, so a coordinator that
    /// resolved Full but never armed leaves the layout snapped, exactly
    /// as if it had never been asked.</summary>
    internal bool Claimed { get; set; }

    /// <summary>Whether this transition still stands (holds the weights
    /// off the model ratio). False once settled, by any path.</summary>
    internal bool IsStanding { get; private set; } = true;

    /// <summary>How many drives landed; diagnostics.</summary>
    internal int Drives { get; private set; }

    /// <summary>Why the transition last settled (drive-finish, tree-op,
    /// drag, bound); diagnostics. Null while standing.</summary>
    internal string? SettleReason { get; private set; }

    /// <summary>
    /// Apply the START geometry: the origin pane whole (the boundary at
    /// the newborn-side far edge of the pre-split rect), the newborn's
    /// cell at zero. The origin's own rect is unchanged from pre-split,
    /// so the hold itself resizes nothing - the first resize is the
    /// first drive.
    /// </summary>
    internal void ApplyStart()
    {
        WriteWeights(1.0);
        _lastWritten = 1.0;
        _boundTimer.Start();
    }

    /// <summary>
    /// Drive the boundary to <paramref name="progress"/> in [0, 1]
    /// along the hold's own axis (0 = the start geometry, 1 = the
    /// model's final ratio): write the interpolated star weights and
    /// settle the layout synchronously, so the drive's geometry is
    /// measured (and presented) before the next frame. Answers false
    /// when the transition is gone (settled, or its grid left the
    /// tree) - the owner stands down, never drives into a dead hold.
    /// A weight the owner did not write since the last drive (a divider
    /// drag landed between frames) settles the transition: the drag
    /// owns the boundary now.
    /// </summary>
    internal bool Drive(double progress)
    {
        if (!IsStanding)
        {
            return false;
        }

        var grid = _host.SplitGridOf(_split);
        if (grid is null)
        {
            Settle("grid-gone");
            return false;
        }

        if (ForeignWeight(grid))
        {
            Settle("drag");
            return false;
        }

        if (PastBound())
        {
            Settle("bound");
            return false;
        }

        var weight = 1.0 + System.Math.Clamp(progress, 0.0, 1.0) * (_split.Ratio - 1.0);
        WriteWeights(weight);
        _lastWritten = weight;
        Drives++;
        _boundTimer.Stop();
        _boundTimer.Start();
        return true;
    }

    /// <summary>
    /// Settle to the model ratio: the split Grid's weights back to the
    /// model's own, the layout settled, the transition retired. The one
    /// consistent state - every path that ends a transition early lands
    /// here, and the reason names which.
    /// </summary>
    internal void Settle(string reason)
    {
        if (!IsStanding)
        {
            return;
        }

        IsStanding = false;
        SettleReason = reason;
        _boundTimer.Stop();
        var grid = _host.SplitGridOf(_split);
        if (grid is not null)
        {
            PaneHost.ApplyRatio(grid, _split);
            _host.UpdateLayout();
        }

        _host.SplitGeometrySettled(this, reason);
    }

    private bool PastBound() => NowMs() - _startedAtMs > SettleBoundMs;

    /// <summary>Whether the grid's boundary weight is one this
    /// transition did not write (and is not the model's own): someone
    /// else - a divider drag - owns the boundary.</summary>
    private bool ForeignWeight(Grid grid)
    {
        var current = BoundaryWeightOf(grid);
        if (double.IsNaN(_lastWritten) || double.IsNaN(current))
        {
            return false;
        }

        return System.Math.Abs(current - _lastWritten) > 1e-4
            && System.Math.Abs(current - _split.Ratio) > 1e-4;
    }

    private double BoundaryWeightOf(Grid grid)
        => _split.Orientation == PaneOrientation.Vertical
            ? grid.ColumnDefinitions.Count == 2
                ? grid.ColumnDefinitions[0].Width.Value
                : double.NaN
            : grid.RowDefinitions.Count == 2
                ? grid.RowDefinitions[0].Height.Value
                : double.NaN;

    private void WriteWeights(double child1Share)
    {
        var grid = _host.SplitGridOf(_split);
        if (grid is null)
        {
            return;
        }

        if (_split.Orientation == PaneOrientation.Vertical
            && grid.ColumnDefinitions.Count == 2)
        {
            grid.ColumnDefinitions[0].Width = new GridLength(child1Share, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = new GridLength(1.0 - child1Share, GridUnitType.Star);
        }
        else if (_split.Orientation != PaneOrientation.Vertical
            && grid.RowDefinitions.Count == 2)
        {
            grid.RowDefinitions[0].Height = new GridLength(child1Share, GridUnitType.Star);
            grid.RowDefinitions[1].Height = new GridLength(1.0 - child1Share, GridUnitType.Star);
        }

        _host.UpdateLayout();
    }
}

/// <summary>
/// The split-geometry hold's host half: the arm point (called from
/// Split, before its synchronous layout), the settle-everything point
/// (called from the rebuild law's settle, so no tree operation ever
/// proceeds past a held layout), and the grid lookup the transition
/// drives through. Partial on the PaneHost.FocusFrame.cs model: the
/// pin's PaneHost.cs never learns this file exists beyond the two
/// one-line call sites the arm and the settle need.
/// </summary>
internal sealed partial class PaneHost
{
    private SplitGeometryTransition? _splitGeometryHold;

    /// <summary>
    /// The split-geometry hold's arm point, called from Split after the
    /// splice and BEFORE the synchronous layout settle: under a
    /// registered coordinator that resolves PaneGeometry Full, the new
    /// split's layout is held at its start geometry (the origin whole,
    /// the newborn's cell zero) for the coordinator to drive; under
    /// anything else (no coordinator, Reduced, Off, RDP) the answer is
    /// null and Split's layout snaps to the model ratio exactly as it
    /// always did. A hold left unclaimed by the time Split returns is
    /// settled at the call site - the fail-safe that keeps a
    /// coordinator that resolved Full but never armed from leaving the
    /// newborn invisible.
    /// </summary>
    internal SplitGeometryTransition? TryHoldSplitBirthGeometry(SplitPane newSplit)
    {
        if (!PaneMotion.Active
            || PaneMotion.Current!.ResolvePolicy(MotionSurfaceClass.PaneGeometry)
                != MotionPolicyLevel.Full)
        {
            return null;
        }

        SettleSplitGeometry("new-split");
        var hold = new SplitGeometryTransition(this, newSplit);
        _splitGeometryHold = hold;
        hold.ApplyStart();
        return hold;
    }

    /// <summary>
    /// Settle the standing split-geometry hold, if one stands: the
    /// rebuild law's settle point (SettlePaneFades) calls this, so no
    /// tree operation - a split, a close, an equalize, a resize, a
    /// zoom, a rehost - ever proceeds past a held layout. Idempotent.
    /// </summary>
    internal void SettleSplitGeometry(string reason)
    {
        if (_splitGeometryHold is { IsStanding: true } standing)
        {
            standing.Settle(reason);
        }

        if (_splitGeometryHold is { IsStanding: false })
        {
            _splitGeometryHold = null;
        }
    }

    /// <summary>The transition's retirement notice: the hold is gone
    /// (settled by its owner, by a tree operation, by a drag, or by its
    /// own bound) and the field is cleared. Extension point the motion
    /// coordinator's adapter subscribes through its own partial; the
    /// pin side has nothing to observe.</summary>
    private void SplitGeometrySettled(SplitGeometryTransition hold, string reason)
    {
        if (ReferenceEquals(_splitGeometryHold, hold))
        {
            _splitGeometryHold = null;
        }

        OnSplitGeometrySettled?.Invoke(this, reason);
    }

    /// <summary>Raised on the UI thread when a split-geometry hold
    /// settles, carrying the reason. A registered motion coordinator's
    /// adapter hears here what it cannot hear from the tree-change
    /// reports (a divider drag, the bound).</summary>
    internal event EventHandler<string>? OnSplitGeometrySettled;

    /// <summary>
    /// The live split Grid a <see cref="SplitPane"/> renders through,
    /// found by the same walk ApplyRatiosRecursive uses: every split
    /// Grid built by BuildVisual carries exactly one Splitter whose
    /// Split is this one. Null when the tree has moved (the grid left
    /// with a rebuild or a close).
    /// </summary>
    internal Grid? SplitGridOf(SplitPane split)
    {
        return _treeRoot is not null && FindSplitGrid(_treeRoot, split) is { } grid
            ? grid
            : null;
    }

    private static Grid? FindSplitGrid(FrameworkElement visual, SplitPane split)
    {
        if (visual is Splitter splitter)
        {
            return ReferenceEquals(splitter.Split, split)
                ? splitter.Parent as Grid
                : null;
        }

        if (visual is not Grid grid)
        {
            return null;
        }

        foreach (var child in grid.Children)
        {
            if (child is Splitter own && ReferenceEquals(own.Split, split))
            {
                return grid;
            }

            if (child is FrameworkElement element
                && FindSplitGrid(element, split) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
