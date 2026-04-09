using System;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Panes;

/// <summary>
/// Surface of <see cref="Ghostty.Panes.PaneHost"/> that
/// <c>Ghostty.Core.Tabs.TabManager</c> consumes. Lives in
/// <c>Ghostty.Core</c> so the test project can reference it
/// without dragging in WinAppSDK. The concrete implementation
/// (<c>Ghostty.Panes.PaneHost</c>) lives in the WinUI project.
///
/// Adding members here means adding them to the production
/// implementation AND any test fakes.
/// </summary>
internal interface IPaneHost
{
    /// <summary>Currently focused leaf, never null after construction.</summary>
    LeafPane ActiveLeaf { get; }

    /// <summary>Total number of leaves currently in the tree.</summary>
    int PaneCount { get; }

    /// <summary>Raised when the active leaf changes.</summary>
    event EventHandler<LeafPane>? LeafFocused;

    /// <summary>Raised when the last leaf in the tree closes.</summary>
    event EventHandler? LastLeafClosed;

    /// <summary>Raised when the active leaf reports a progress state
    /// change via OSC 9;4. Only the active leaf's progress is
    /// forwarded — background panes update their own
    /// <c>TerminalControl.CurrentProgress</c> but do not drive the
    /// tab-level indicator.</summary>
    event EventHandler<TabProgressState>? ProgressChanged;

    /// <summary>Close the currently active pane.</summary>
    void CloseActive();

    /// <summary>Free every leaf's libghostty surface. Called by the
    /// owning tab when it is being destroyed.</summary>
    void DisposeAllLeaves();
}
