using Ghostty.Controls;
using Ghostty.Core.Config;
using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;
using Ghostty.Hosting;
using Ghostty.Panes;

namespace Ghostty.Tabs;

/// <summary>
/// Single place that knows how to construct a <see cref="PaneHost"/>.
/// Used by both <see cref="MainWindow"/>'s initial tab creation and
/// <see cref="Core.Tabs.TabManager.NewTab"/>. References WinUI types,
/// so it lives in the Ghostty WinUI project, not in Ghostty.Core.
///
/// The construction shape mirrors what MainWindow did before tabs
/// existed: one PaneHost, terminalFactory creates a fresh
/// TerminalControl per leaf.
/// </summary>
internal sealed class PaneHostFactory
{
    private readonly GhosttyHost _host;
    private readonly IConfigService _config;

    public PaneHostFactory(GhosttyHost host, IConfigService config)
    {
        _host = host;
        _config = config;
    }

    public IPaneHost Create(ProfileSnapshot? snapshot = null) =>
        new PaneHost(
            _host,
            terminalFactory: snap => new TerminalControl { Snapshot = snap },
            initialSnapshot: snapshot,
            // Read the undo-timeout live per tab so a config reload takes
            // effect on newly created tabs. Resolved through the pure Core
            // helper, which maps 0 to a disabled policy (upstream fidelity)
            // and a negative/corrupt value to the safe 5s default.
            undoPolicy: UndoPolicy.FromConfigMilliseconds(_config.UndoTimeoutMs));

    /// <summary>
    /// Build a host seeded from a restored tree. Leaves carry their
    /// <see cref="LeafPane.Snapshot"/> (Tag null); the host creates the
    /// TerminalControls. <paramref name="activeLeaf"/> must be a leaf of
    /// <paramref name="root"/>; <paramref name="zoomedLeaf"/> re-zooms when
    /// non-null and present.
    /// </summary>
    public IPaneHost CreateFromTree(PaneNode root, LeafPane activeLeaf, LeafPane? zoomedLeaf) =>
        new PaneHost(
            _host,
            terminalFactory: snap => new TerminalControl { Snapshot = snap },
            root: root,
            activeLeaf: activeLeaf,
            zoomedLeaf: zoomedLeaf,
            undoPolicy: UndoPolicy.FromConfigMilliseconds(_config.UndoTimeoutMs));
}
