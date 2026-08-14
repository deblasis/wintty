namespace Ghostty.Core.Shell;

/// <summary>What the caller should do with the launch icon.</summary>
public enum LaunchIconOutcome
{
    /// <summary>Already dismissed by an earlier signal. Do nothing.</summary>
    Ignore,

    /// <summary>Start the fade now.</summary>
    FadeNow,

    /// <summary>Start the fade after <see cref="LaunchIconDecision.DelayMs"/>.</summary>
    FadeAfter,
}

/// <param name="Outcome">What to do.</param>
/// <param name="DelayMs">Milliseconds to wait, zero unless <see cref="LaunchIconOutcome.FadeAfter"/>.</param>
public readonly record struct LaunchIconDecision(LaunchIconOutcome Outcome, int DelayMs);

/// <summary>
/// Decides when the cold-start launch splash leaves the screen. Pure:
/// the caller owns the clock and the timers and passes elapsed time in,
/// so every branch here is directly testable.
///
/// The only thing this arbitrates is the minimum dwell. The hard
/// timeout lives with the splash window itself, which is the thing that
/// must never get stuck on screen, and so is the thing that should own
/// its own escape hatch.
/// </summary>
public sealed class LaunchIconPolicy
{
    /// <summary>
    /// Shortest time the splash stays up. On a fast start the window can
    /// have content in well under 100 ms; without a floor the splash
    /// would flash for a frame or two and read as a glitch rather than a
    /// beat.
    /// </summary>
    public const int MinVisibleMs = 400;

    private bool _dismissed;

    /// <summary>
    /// The window has content worth revealing. Latches, so a second call
    /// cannot start a second dismissal.
    /// </summary>
    /// <param name="elapsedMs">Milliseconds the splash has been on screen.</param>
    public LaunchIconDecision Ready(int elapsedMs)
    {
        if (_dismissed) return new LaunchIconDecision(LaunchIconOutcome.Ignore, 0);
        _dismissed = true;

        var shown = elapsedMs < 0 ? 0 : elapsedMs;
        if (shown >= MinVisibleMs)
            return new LaunchIconDecision(LaunchIconOutcome.FadeNow, 0);

        return new LaunchIconDecision(LaunchIconOutcome.FadeAfter, MinVisibleMs - shown);
    }
}
