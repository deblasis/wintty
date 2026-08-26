using System;
using System.Collections.Generic;

namespace Ghostty.Core.Windows;

/// <summary>
/// Picks the window a process-wide request should act on. Pure, and public
/// for the same reason <c>PipeServerRetryPolicy</c> is: the decision belongs
/// to a service the test project cannot construct (it does not reference the
/// WinUI shell at all), so leaving it inline in the shell means it is never
/// exercised by anything but a human with two windows open.
///
/// The rule is "the window the user last looked at, if it can still take the
/// request; otherwise any other that can". Falling back matters more than it
/// looks: the last-activated window is remembered across its own close on
/// some paths, and a request routed to it then reaches a window whose
/// surfaces are already gone.
/// </summary>
public static class ActiveWindowTarget
{
    /// <summary>
    /// <paramref name="lastActivated"/> when it passes
    /// <paramref name="eligible"/>, otherwise the first of
    /// <paramref name="all"/> that does, otherwise null.
    /// </summary>
    /// <param name="lastActivated">
    /// The window that most recently received activation, or null when the
    /// process has never had one (or the last one closed).
    /// </param>
    /// <param name="all">Every live window, in no particular order.</param>
    /// <param name="eligible">
    /// Whether a window can take the request right now. Both halves are
    /// filtered through it, so an ineligible <paramref name="lastActivated"/>
    /// does not short-circuit the search.
    /// </param>
    public static T? Choose<T>(T? lastActivated, IEnumerable<T> all, Func<T, bool> eligible)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(eligible);

        if (lastActivated is not null && eligible(lastActivated)) return lastActivated;

        foreach (var candidate in all)
        {
            if (candidate is null) continue;
            // Skipped rather than returned: it already failed the predicate
            // above, and the enumeration is the only place it can reappear.
            if (ReferenceEquals(candidate, lastActivated)) continue;
            if (eligible(candidate)) return candidate;
        }

        return null;
    }
}
