using System;
using System.Collections.Generic;
using Ghostty.Core.Profiles.Tracking;

namespace Ghostty.Tests.Profiles.Tracking;

/// <summary>
/// Test double: tests drive change events by hand via <see cref="Raise"/>.
/// Tracks which root PIDs have been registered so tests can assert the
/// caller is wiring registration / unregistration correctly.
/// </summary>
public sealed class FakeActiveProcessTracker : IActiveProcessTracker
{
    private readonly HashSet<int> _registered = new();

    public event EventHandler<ActiveProcessChangedEventArgs>? Changed;

    public IReadOnlyCollection<int> Registered => _registered;
    public bool IsDisposed { get; private set; }

    public void Register(int rootPid) => _registered.Add(rootPid);
    public void Unregister(int rootPid) => _registered.Remove(rootPid);

    public void Raise(int rootPid, string? exeBasename, string? commandLine) =>
        Changed?.Invoke(this, new ActiveProcessChangedEventArgs(rootPid, exeBasename, commandLine));

    public void Dispose() => IsDisposed = true;
}
