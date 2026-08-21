using System;
using System.Collections.Generic;

namespace Ghostty.Core.Commands;

/// <summary>
/// Which item in a palette list is current, after an arrow key or after the
/// list underneath the selection was rebuilt.
///
/// Here rather than in the view model because that is what makes the rules
/// testable at all: the view model lives in the WinUI project, which the
/// test assembly cannot reference.
/// </summary>
public static class PaletteSelection
{
    /// <summary>
    /// The item <paramref name="delta"/> steps away from
    /// <paramref name="current"/>, clamped to the ends of the list.
    ///
    /// A null or no-longer-present <paramref name="current"/> resolves to the
    /// first item, in either direction: pressing Up in a list nothing is
    /// selected in should land somewhere, and the top is the only end both
    /// keys agree on.
    /// </summary>
    public static T? Step<T>(IReadOnlyList<T> items, T? current, int delta)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return null;

        var index = IndexOf(items, current);
        if (index < 0) return items[0];

        // Widened before the clamp. A large delta from a paging binding
        // overflows int arithmetic and lands on the first item, which is the
        // opposite end from the one the caller asked for.
        var next = Math.Clamp((long)index + delta, 0, items.Count - 1);
        return items[(int)next];
    }

    /// <summary>
    /// The selection for a freshly built list: its first item, or none when
    /// it is empty.
    ///
    /// Exists so that "a list and the selection into it are set together" is
    /// one call rather than a convention. Assigning the selection only when
    /// the list turned out non-empty leaves the previous one in place behind
    /// a query that matched nothing, and Enter then runs a command that is
    /// not on screen.
    /// </summary>
    public static T? SelectTop<T>(IReadOnlyList<T> items) where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count > 0 ? items[0] : null;
    }

    private static int IndexOf<T>(IReadOnlyList<T> items, T? value) where T : class
    {
        if (value is null) return -1;

        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < items.Count; i++)
        {
            if (comparer.Equals(items[i], value)) return i;
        }
        return -1;
    }
}
