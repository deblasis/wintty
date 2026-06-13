using System.Collections.Generic;
using Ghostty.Core.Taskbar;

namespace Ghostty.Tests.Taskbar;

/// <summary>Recording fake for <see cref="ITaskbarOverlaySink"/>.
/// Stores every <see cref="SetAttention"/> argument in order.</summary>
internal sealed class FakeTaskbarOverlaySink : ITaskbarOverlaySink
{
    public List<bool> Writes { get; } = new();
    public void SetAttention(bool active) => Writes.Add(active);
}
