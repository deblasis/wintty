using Ghostty.Controls;
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

    public PaneHostFactory(GhosttyHost host) { _host = host; }

    public IPaneHost Create(ProfileSnapshot? snapshot = null) =>
        new PaneHost(
            _host,
            terminalFactory: snap => new TerminalControl { Snapshot = snap },
            initialSnapshot: snapshot);
}
