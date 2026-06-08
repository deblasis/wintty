namespace Ghostty.Core.Panes;

/// <summary>
/// Which structural operation a <see cref="PaneSnapshot"/> precedes.
/// Used by <see cref="PaneHistory"/> to coalesce resize bursts; the
/// other kinds are recorded one entry per op.
/// </summary>
internal enum PaneOpKind { Split, Close, Equalize, Resize, Zoom }

/// <summary>
/// Immutable capture of the pane-tree model taken BEFORE an undoable
/// operation mutates it. <see cref="Root"/> is a structural clone (see
/// <see cref="PaneTree.Clone"/>) whose leaf identities match the live
/// tree, so restoring it rebuilds the same TerminalControls.
/// </summary>
internal sealed record PaneSnapshot(
    PaneNode Root,
    LeafPane Active,
    LeafPane? Zoomed,
    PaneOpKind Kind);
