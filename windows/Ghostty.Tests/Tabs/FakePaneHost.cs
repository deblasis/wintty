using System;
using Ghostty.Core.Panes;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// In-memory implementation of <see cref="IPaneHost"/> for unit
/// tests. Tracks pane count via a counter, raises events on demand,
/// records calls. Implements the real interface from Ghostty.Core,
/// not a parallel duplicate of PaneHost.
///
/// <see cref="LeafPane.Tag"/> is left null: tests never call
/// <c>leaf.Terminal()</c> because TabManager uses leaves only for
/// reference equality and as event payloads.
/// </summary>
internal sealed class FakePaneHost : IPaneHost
{
    public LeafPane ActiveLeaf { get; } = new LeafPane();
    public int PaneCount { get; private set; } = 1;
    public int CloseActiveCalls { get; private set; }
    public int DisposeAllCalls { get; private set; }

    public event EventHandler<LeafPane>? LeafFocused;
    public event EventHandler? LastLeafClosed;

    public void CloseActive()
    {
        CloseActiveCalls++;
        if (PaneCount > 0) PaneCount--;
        if (PaneCount == 0) LastLeafClosed?.Invoke(this, EventArgs.Empty);
    }

    public void DisposeAllLeaves()
    {
        DisposeAllCalls++;
        PaneCount = 0;
    }

    public void SetPaneCount(int n) => PaneCount = n;
    public void RaiseLeafFocused() => LeafFocused?.Invoke(this, ActiveLeaf);
}
