using System.Collections.Generic;
using Ghostty.Core.Taskbar;

namespace Ghostty.Tests.Taskbar;

/// <summary>Records every overlay write in order; null kind is a clear.</summary>
internal sealed class FakeTaskbarBadgeSink : ITaskbarBadgeSink
{
    public List<(TaskbarBadgeKind? Kind, string Description)> Writes { get; } = new();
    public void Show(TaskbarBadgeKind kind, string description) => Writes.Add((kind, description));
    public void Clear() => Writes.Add((null, string.Empty));
    public TaskbarBadgeKind? Current => Writes.Count == 0 ? null : Writes[^1].Kind;
}
