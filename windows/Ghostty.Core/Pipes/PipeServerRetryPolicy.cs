using System;

namespace Ghostty.Core.Pipes;

/// <summary>
/// What ended one iteration of a single-instance named-pipe server loop.
/// </summary>
public enum PipeLoopOutcome
{
    /// <summary>Cancellation was requested.</summary>
    Cancelled,

    /// <summary>
    /// The server stream could not be created (e.g. the pipe name is already
    /// owned by another instance in this process). Permanent for this loop.
    /// </summary>
    ServerCreationFailed,

    /// <summary>A client connected and the session completed normally.</summary>
    SessionEnded,

    /// <summary>A connected session broke with an I/O error mid-stream.</summary>
    SessionFaulted,
}

/// <summary>What the server loop should do next.</summary>
public enum PipeLoopDecision
{
    /// <summary>Exit the loop (cancellation).</summary>
    Stop,

    /// <summary>Exit the loop permanently; retrying cannot succeed.</summary>
    StandDown,

    /// <summary>Accept the next connection immediately.</summary>
    RetryImmediately,

    /// <summary>Wait <see cref="PipeServerRetryPolicy.Backoff"/> then retry.</summary>
    RetryAfterBackoff,
}

/// <summary>
/// Decides how a single-instance named-pipe server loop reacts to each
/// iteration outcome. Pure and stateful only in the consecutive-fault count,
/// so it is unit-testable without any pipe or threading machinery.
///
/// Exists because the original inline loop treated <em>every</em>
/// <see cref="System.IO.IOException"/> the same: a server-creation failure
/// ("All pipe instances are busy") was retried immediately, busy-looping
/// forever and filling the disk with rolled log files. This policy makes the
/// distinctions explicit and bounds <em>both</em> failure paths so no outcome
/// can produce an unbounded hot loop.
///
/// Not thread-safe: the consecutive-fault count is mutated without locking.
/// Drive it from a single server loop (one <see cref="Decide"/> call at a
/// time), which is how <c>ThemePreviewService</c> uses it.
/// </summary>
public sealed class PipeServerRetryPolicy
{
    private readonly int _maxConsecutiveFaults;
    private readonly TimeSpan _backoff;
    private int _consecutiveFaults;

    public PipeServerRetryPolicy(int maxConsecutiveFaults = 10, TimeSpan? backoff = null)
    {
        _maxConsecutiveFaults = maxConsecutiveFaults < 1 ? 1 : maxConsecutiveFaults;
        _backoff = backoff ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>Delay applied for a <see cref="PipeLoopDecision.RetryAfterBackoff"/>.</summary>
    public TimeSpan Backoff => _backoff;

    public PipeLoopDecision Decide(PipeLoopOutcome outcome)
    {
        switch (outcome)
        {
            case PipeLoopOutcome.Cancelled:
                return PipeLoopDecision.Stop;

            // Creation can never succeed on retry for this loop (the name is
            // taken). Standing down is the whole point of the fix.
            case PipeLoopOutcome.ServerCreationFailed:
                return PipeLoopDecision.StandDown;

            // A clean session resets the fault budget and we accept the next
            // client right away.
            case PipeLoopOutcome.SessionEnded:
                _consecutiveFaults = 0;
                return PipeLoopDecision.RetryImmediately;

            // Faults back off, but a pipe that fails on every reconnect must
            // eventually give up rather than retry forever.
            case PipeLoopOutcome.SessionFaulted:
                _consecutiveFaults++;
                return _consecutiveFaults >= _maxConsecutiveFaults
                    ? PipeLoopDecision.StandDown
                    : PipeLoopDecision.RetryAfterBackoff;

            default:
                return PipeLoopDecision.Stop;
        }
    }
}
