using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Ghostty.Core.Panes;

namespace Ghostty.Motion;

/// <summary>
/// Which pane fade is standing, if any. The split fades its new
/// pane in; a soft close fades its pane away before the cut. A hard
/// close arms none: nothing exists to fade.
/// </summary>
internal enum PaneFadeKind
{
    None,
    SplitIn,
    SoftCloseOut,
}

/// <summary>
/// The pane fades' in-flight bookkeeping, and the law the shell's tree
/// mutations ride. A fade is keyed on its <see cref="LeafPane"/>: one
/// fade per pane at a time, and a pane whose fade stands is settled
/// BEFORE any rebuild, rehost, restore or teardown touches it -- the
/// settle write stops the board and puts the control back the way it
/// rests (opacity 1, hit-testable). When the write lands is the
/// tracker's; what it does is the shell's: PaneHost installs
/// <see cref="Settle"/> once, and the test assembly installs a fake.
///
/// Nothing here holds a control alive: the table is weak on the leaf,
/// so a pane whose host died without settling still stops counting.
/// </summary>
internal static class PaneFadeTracker
{
    /// <summary>
    /// The fade cap, in milliseconds. Every pane fade runs no longer
    /// than this, and the boards take their duration from this constant,
    /// so the cap is one value in one place.
    /// </summary>
    public const double FadeMs = 120;

    /// <summary>
    /// The settle write: end a fade out of turn. Installed once by
    /// PaneHost's type initializer. Unset, <see cref="SettleAll"/> still
    /// ends every flight (the tests run without a shell); it only writes
    /// nothing.
    /// </summary>
    internal static Action<LeafPane, PaneFadeKind>? Settle;

    // One row per leaf with a fade standing. ConditionalWeakTable values
    // must be reference types, hence the box.
    private static readonly ConditionalWeakTable<LeafPane, Box> Flights = new();

    private sealed class Box(PaneFadeKind kind)
    {
        public readonly PaneFadeKind Kind = kind;
    }

    /// <summary>
    /// Record a fade as standing on the leaf. False when one already
    /// stands: a pane runs one fade at a time, and the standing one
    /// finishes as it is. <see cref="PaneFadeKind.None"/> begins nothing
    /// -- it is the plain cut.
    /// </summary>
    public static bool Begin(LeafPane leaf, PaneFadeKind kind)
    {
        if (kind == PaneFadeKind.None || Flights.TryGetValue(leaf, out _))
        {
            return false;
        }

        Flights.Add(leaf, new Box(kind));
        return true;
    }

    /// <summary>Whether a fade stands on the leaf right now.</summary>
    public static bool IsFading(LeafPane leaf) => Flights.TryGetValue(leaf, out _);

    /// <summary>
    /// The kind standing on the leaf; <see cref="PaneFadeKind.None"/>
    /// when clear.
    /// </summary>
    public static PaneFadeKind KindOf(LeafPane leaf) =>
        Flights.TryGetValue(leaf, out var box) ? box.Kind : PaneFadeKind.None;

    /// <summary>How many fades stand, across every pane host.</summary>
    public static int Standing
    {
        get
        {
            var count = 0;
            foreach (var _ in Flights)
            {
                count++;
            }

            return count;
        }
    }

    /// <summary>
    /// A flight's natural finish: its board ran to its end (or was
    /// stopped after completing). Clears the flight; the caller performs
    /// the restore writes.
    /// </summary>
    public static void Settled(LeafPane leaf) => Flights.Remove(leaf);

    /// <summary>
    /// End every standing fade now, through the settle write, and return
    /// the flights that stood. This is the rebuild law's whole body: a
    /// rebuild, rehost, restore, zoom or close runs it first, so no
    /// control is ever remounted mid-fade and every settled control reads
    /// opacity 1 again before the tree is touched. The flights clear
    /// BEFORE the writes run, so a write that stops a board whose
    /// Completed handler then fires finds no flight and stands down.
    /// </summary>
    public static IReadOnlyList<(LeafPane Leaf, PaneFadeKind Kind)> SettleAll()
    {
        var ended = new List<(LeafPane, PaneFadeKind)>();
        foreach (var row in Flights)
        {
            ended.Add((row.Key, row.Value.Kind));
        }

        Flights.Clear();
        foreach (var (leaf, kind) in ended)
        {
            Settle?.Invoke(leaf, kind);
        }

        return ended;
    }

    /// <summary>
    /// Test isolation: the tracker is process-static, and every fact
    /// here runs against an empty table.
    /// </summary>
    internal static void Clear() => Flights.Clear();
}
