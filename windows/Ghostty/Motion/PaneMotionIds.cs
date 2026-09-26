using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Ghostty.Core.Panes;

namespace Ghostty.Motion;

/// <summary>
/// The opaque per-leaf integers the pane lifecycle surface reports. An id
/// is assigned once per <see cref="LeafPane"/> and stays with the leaf for
/// its lifetime, so within one reported transition the same id before and
/// after means "the same pane survived the rebuild" and different ids mean
/// different panes. The numbers carry no other meaning: they are not
/// stable across processes, not dense, and not comparable across leaves
/// that never met in one transition.
///
/// The map is side-mounted (ConditionalWeakTable) so reporting never keeps
/// a closed pane alive: the entry vanishes with the leaf, and since a
/// leaf is never reused after it leaves the tree, an id is never handed
/// to a second pane.
/// </summary>
internal static class PaneMotionIds
{
    private static int _next;

    private sealed class Box
    {
        public Box(int value) => Value = value;
        public int Value { get; }
    }

    private static readonly ConditionalWeakTable<LeafPane, Box> Ids = new();

    public static int Of(LeafPane leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        return Ids.GetValue(leaf, static _ => new Box(Interlocked.Increment(ref _next))).Value;
    }
}
