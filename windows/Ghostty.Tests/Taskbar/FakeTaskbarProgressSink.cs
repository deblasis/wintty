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
    public TabProgressState Last => Writes.Count == 0 ? TabProgressState.None : Writes[^1];
    public void Write(TabProgressState state) => Writes.Add(state);
    public void Reset() => Writes.Clear();
}
