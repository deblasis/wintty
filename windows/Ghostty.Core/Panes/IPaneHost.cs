using System;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Panes;

/// <summary>
/// Surface of <see cref="Ghostty.Panes.PaneHost"/> that
/// <c>Ghostty.Core.Tabs.TabManager</c> consumes. The concrete
/// implementation (<c>Ghostty.Panes.PaneHost</c>) lives in the WinUI
/// project.
///
/// Adding members here means adding them to the production
/// implementation AND any test fakes.
/// </summary>
internal interface IPaneHost
{
    /// <summary>Currently focused leaf, never null after construction.</summary>
    LeafPane ActiveLeaf { get; }

    /// <summary>Root of the split tree (a single leaf when unsplit).</summary>
    PaneNode RootNode { get; }

    /// <summary>The zoomed leaf, or null when no pane is zoomed.</summary>
    LeafPane? ZoomedLeaf { get; }

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

    /// <summary>Raised when the active leaf rings the bell (libghostty
    /// ring-bell action). Only the active leaf is forwarded, matching
    /// <see cref="ProgressChanged"/>; background panes ring audibly but
    /// do not drive the window/tab indicators. Carries the decoded
    /// <c>bell-features</c> so each consumer gates itself: the taskbar
    /// attention badge on <c>attention</c>, the tab title glyph on
    /// <c>title</c>.</summary>
    event EventHandler<Ghostty.Core.Bell.BellFeatures>? BellRang;

    /// <summary>Raised when the active leaf's surface acknowledges the
    /// bell (gained focus or took a keystroke). Clears the tab title
    /// indicator. The window attention badge clears separately on window
    /// activation.</summary>
    event EventHandler? BellAcknowledged;

    /// <summary>
    /// Raised after any structural change to the tree (split, close,
    /// zoom toggle, equalize, resize, undo/redo restore). The session
    /// manager coalesces this into a debounced persist.
    /// </summary>
    event EventHandler? LayoutChanged;

    /// <summary>
    /// Split the active leaf with the given orientation. The new leaf
    /// becomes the active leaf. <paramref name="snapshot"/> is recorded
    /// on the freshly-created leaf.
    /// </summary>
    void Split(PaneOrientation orientation, ProfileSnapshot? snapshot);

    /// <summary>Close the currently active pane.</summary>
    void CloseActive();

    /// <summary>Free every leaf's libghostty surface. Called by the
    /// owning tab when it is being destroyed.</summary>
    void DisposeAllLeaves();
}
