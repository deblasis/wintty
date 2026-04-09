using System;
using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Ghostty.Core.Taskbar;

namespace Ghostty.Tests.Taskbar;

/// <summary>
/// Recording fake for <see cref="ITaskbarProgressSink"/>. Stores
/// every <see cref="Write"/> call in order so tests can assert
/// the full sequence the coordinator emitted.
/// </summary>
internal sealed class FakeTaskbarProgressSink : ITaskbarProgressSink
{
    public List<TabProgressState> Writes { get; } = new();

    /// <summary>Last recorded write. Throws if nothing has been
    /// written yet so callers can't silently conflate "no writes"
    /// with "wrote None".</summary>
    public TabProgressState Last =>
        Writes.Count == 0
            ? throw new InvalidOperationException("No writes recorded yet.")
            : Writes[^1];

    public void Write(TabProgressState state) => Writes.Add(state);
    public void Reset() => Writes.Clear();
}
