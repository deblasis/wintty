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
            // helper so a degenerate (<=0) value falls back to the 5s default.
            undoTimeout: UndoTimeout.FromMilliseconds(_config.UndoTimeoutMs));
}
