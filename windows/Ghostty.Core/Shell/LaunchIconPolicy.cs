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
/// Decides when the cold-start launch icon leaves the screen. Pure: the
/// caller owns the clock and the timers and passes elapsed time in, so
/// every branch here is directly testable.
///
/// Two signals can dismiss the icon -- the first surface reporting that
/// it has rendered, and a watchdog for the case where it never does.
/// Whichever arrives first latches, so the two can never both fade.
/// </summary>
public sealed class LaunchIconPolicy
{
    /// <summary>
    /// Shortest time the icon stays up. On a warm disk the first render
    /// can land in well under 100 ms; without a floor the icon would
    /// flash for a frame or two and read as a glitch rather than a beat.
    /// </summary>
    public const int MinVisibleMs = 400;

    /// <summary>
    /// Longest the icon stays up with no first-render signal at all
    /// (surface creation failed, the shell wedged). Without this the
    /// window would sit behind the icon forever.
    /// </summary>
    public const int WatchdogMs = 3000;

    private bool _dismissed;

    /// <summary>The first surface reported that it has rendered.</summary>
    /// <param name="elapsedMs">Milliseconds the icon has been on screen.</param>
    public LaunchIconDecision Ready(int elapsedMs)
    {
        if (_dismissed) return new LaunchIconDecision(LaunchIconOutcome.Ignore, 0);
        _dismissed = true;

        var shown = elapsedMs < 0 ? 0 : elapsedMs;
        if (shown >= MinVisibleMs)
            return new LaunchIconDecision(LaunchIconOutcome.FadeNow, 0);

        return new LaunchIconDecision(LaunchIconOutcome.FadeAfter, MinVisibleMs - shown);
    }

    /// <summary>The watchdog expired with no first-render signal.</summary>
    public LaunchIconDecision Timeout()
    {
        if (_dismissed) return new LaunchIconDecision(LaunchIconOutcome.Ignore, 0);
        _dismissed = true;
        return new LaunchIconDecision(LaunchIconOutcome.FadeNow, 0);
    }
}
