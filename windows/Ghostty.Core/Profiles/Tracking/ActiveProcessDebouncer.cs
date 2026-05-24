using System;
using System.Collections.Generic;

namespace Ghostty.Core.Profiles.Tracking;

/// <summary>
/// Per-root debouncer: a new (exe, cmdline) value must be observed for
/// at least <c>windowMs</c> milliseconds of wall-clock time before it is
/// emitted to subscribers. Absorbs sub-window churn from rapid command
/// sequences (e.g. <c>ls &amp;&amp; cd ... &amp;&amp; ls</c>).
///
/// Pure logic; the caller passes the current time in. Tests use a fake
/// clock; the production tracker passes <see cref="System.Environment.TickCount64"/>.
/// </summary>
public sealed class ActiveProcessDebouncer
{
    private readonly int _windowMs;
    private readonly Dictionary<int, RootState> _state = new();

    public ActiveProcessDebouncer(int windowMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowMs);
        _windowMs = windowMs;
    }

    /// <summary>
    /// Observe the current foreground process for a root. Returns a
    /// <see cref="DebouncerEmission"/> when the value has been stable for
    /// the window AND differs from the last-emitted value for this root;
    /// otherwise null.
    /// </summary>
    /// <remarks>
    /// nowMs must come from a monotonic clock (e.g. Environment.TickCount64).
    /// Mixing tick sources or wall-clock-with-NTP-adjustments will misalign
    /// the stability window.
    /// </remarks>
    public DebouncerEmission? Observe(int rootPid, string? exeBasename, string? commandLine, long nowMs)
    {
        if (!_state.TryGetValue(rootPid, out var s))
        {
            s = new RootState();
            _state[rootPid] = s;
        }

        // No change vs. the pending candidate: keep waiting.
        if (s.PendingExe == exeBasename && s.PendingCmdline == commandLine)
        {
            if (s.PendingSinceMs is { } since && nowMs - since >= _windowMs)
            {
                if (s.EmittedExe != exeBasename || s.EmittedCmdline != commandLine)
                {
                    s.EmittedExe = exeBasename;
                    s.EmittedCmdline = commandLine;
                    s.PendingSinceMs = null;
                    return new DebouncerEmission(rootPid, exeBasename, commandLine);
                }
                // Window elapsed but value matches what we already emitted.
                // Suppress further emission until the value actually changes.
                s.PendingSinceMs = null;
            }
            return null;
        }

        // Value changed: restart the window.
        s.PendingExe = exeBasename;
        s.PendingCmdline = commandLine;
        s.PendingSinceMs = nowMs;
        return null;
    }

    /// <summary>
    /// Drop state for a root that has gone away (typically called when
    /// a tab closes). Subsequent Observe calls for the same rootPid start
    /// fresh.
    /// </summary>
    public void Forget(int rootPid) => _state.Remove(rootPid);

    private sealed class RootState
    {
        public string? PendingExe;
        public string? PendingCmdline;
        public long? PendingSinceMs;
        public string? EmittedExe;
        public string? EmittedCmdline;
    }
}

public sealed record DebouncerEmission(int RootPid, string? ExeBasename, string? CommandLine);
