using System;
using System.Collections.Generic;

namespace Ghostty.Core.Panes;

/// <summary>
/// Bounded LIFO of recently-closed items (tabs or windows), for the
/// reopen-closed-tab / reopen-closed-window commands. Pushing past
/// <see cref="_capacity"/> evicts the oldest entry so the stack never
/// grows unbounded. Session-scoped, in memory; no time expiry — the
/// snapshots hold no live surfaces, so retention is cheap. Pure logic,
/// no WinUI; unit-tested directly. Distinct from the disk session
/// persistence (which keeps a single snapshot for next-launch restore);
/// this is the same-session reopen history.
/// </summary>
internal sealed class ClosedStack<T>
{
    private readonly int _capacity;

    // Most-recently-pushed at the end; a LinkedList gives O(1) eviction of
    // the oldest (front) entry when capacity is exceeded.
    private readonly LinkedList<T> _items = new();

    public ClosedStack(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _items.Count;

    public void Push(T item)
    {
        _items.AddLast(item);
        if (_items.Count > _capacity)
            _items.RemoveFirst();
    }

    /// <summary>
    /// Remove and return the most-recently-pushed item. Returns false with
    /// <paramref name="item"/> set to <c>default</c> when the stack is empty;
    /// callers must check the return value before using <paramref name="item"/>.
    /// </summary>
    public bool TryPop(out T item)
    {
        if (_items.Last is { } last)
        {
            item = last.Value;
            _items.RemoveLast();
            return true;
        }
        item = default!;
        return false;
    }
}
