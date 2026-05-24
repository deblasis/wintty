using System;

namespace Ghostty.Core.Profiles.Tracking;

/// <summary>
/// Tracks the foreground command running inside each registered pty.
/// One instance per app; tabs register their shell-process PID at
/// construction and unregister at dispose. The tracker raises
/// <see cref="Changed"/> when the innermost descendant process of any
/// registered root changes (after the debouncer window).
///
/// The interface is platform-agnostic so Core can wire it up; the
/// production implementation (Windows polling via Toolhelp32) lives in
/// <c>WindowsActiveProcessTracker</c>.
/// </summary>
public interface IActiveProcessTracker : IDisposable
{
    event EventHandler<ActiveProcessChangedEventArgs>? Changed;

    /// <summary>
    /// Begin tracking the descendants of <paramref name="rootPid"/>.
    /// Registering the same PID twice is a no-op.
    /// </summary>
    void Register(int rootPid);

    /// <summary>
    /// Stop tracking the descendants of <paramref name="rootPid"/>.
    /// Subsequent ticks ignore this PID. Unregistering an unknown PID
    /// is a no-op.
    /// </summary>
    void Unregister(int rootPid);
}

public sealed record ActiveProcessChangedEventArgs(
    int RootPid,
    string? ExeBasename,
    string? CommandLine);
