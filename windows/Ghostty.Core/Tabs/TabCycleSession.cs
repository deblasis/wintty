using System;
using System.Collections.Generic;

namespace Ghostty.Core.Tabs;

/// <summary>
/// An Alt+Tab cursor over a frozen MRU snapshot. The snapshot is taken
/// when the cycle begins (most-recently-used first, index 0 = the tab that
/// was active when the cycle started) and does NOT change while cycling, so
/// repeated taps walk a stable list. The first forward
/// <see cref="Advance"/> lands on index 1 - the previously-active tab, the
/// dominant "flip to my last tab" case; the first reverse advance lands on
/// the last entry.
/// </summary>
internal sealed class TabCycleSession<T>
{
    private readonly IReadOnlyList<T> _frozen;
    private int _cursor;

    public TabCycleSession(IReadOnlyList<T> frozenOrder)
    {
        if (frozenOrder.Count == 0)
            throw new ArgumentException("Cycle snapshot must not be empty.", nameof(frozenOrder));
        _frozen = frozenOrder;
        _cursor = 0;
    }

    public T Current => _frozen[_cursor];

    /// <summary>Move the cursor one step (wrapping) and return the new highlight.</summary>
    public T Advance(bool forward)
    {
        var n = _frozen.Count;
        _cursor = forward
            ? (_cursor + 1) % n
            : (_cursor - 1 + n) % n;
        return _frozen[_cursor];
    }
}
