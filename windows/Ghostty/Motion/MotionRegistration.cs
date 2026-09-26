using System;
using System.Threading;

namespace Ghostty.Motion;

/// <summary>
/// The process-wide registration point for <see cref="IPaneMotionCoordinator"/>.
/// Default: none registered -- every notification call site checks
/// <see cref="Active"/> (or reads <see cref="Current"/>) and otherwise
/// does nothing. One registration max per process, and registration is
/// set-once: <see cref="Register"/> wins a single compare-exchange slot,
/// so a concurrent loser (or any later caller) gets
/// <see cref="InvalidOperationException"/> rather than a silent
/// replacement, and the value the winner installed is the one every
/// reader sees afterwards.
/// </summary>
internal static class PaneMotion
{
    private static IPaneMotionCoordinator? _current;

    /// <summary>Whether a coordinator is registered. Equivalent to
    /// <c>Current is not null</c>, stated so notification call sites can
    /// read as a guard rather than a null test.</summary>
    public static bool Active => Current is not null;

    /// <summary>The registered coordinator, or null when none is.</summary>
    public static IPaneMotionCoordinator? Current => _current;

    /// <summary>
    /// Registers <paramref name="coordinator"/> as the process's single
    /// observer. Thread-safe and set-once: the first call wins, and any
    /// later call throws <see cref="InvalidOperationException"/> instead
    /// of replacing the winner. Null is refused outright -- "no
    /// coordinator" is the default state, not something to store.
    /// </summary>
    public static void Register(IPaneMotionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);

        if (Interlocked.CompareExchange(ref _current, coordinator, null) is not null)
        {
            throw new InvalidOperationException(
                "A pane-motion coordinator is already registered.");
        }
    }

    /// <summary>
    /// Clears the registration. Test-only: production code registers at
    /// most once per process and never clears, but without this the
    /// set-once contract would make its own tests order-dependent on a
    /// shared static. Like <see cref="Register"/>, thread-safe.
    /// </summary>
    internal static void ResetForTests()
        => Interlocked.Exchange(ref _current, null);
}
