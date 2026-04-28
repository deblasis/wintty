using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Wintty")]
[assembly: InternalsVisibleTo("Ghostty.Tests")]

namespace Ghostty.Core.Panes;

internal abstract class PaneNode { }

internal sealed class LeafPane : PaneNode
{
    /// <summary>
    /// Opaque handle for the WinUI side to attach its per-leaf
    /// <c>TerminalControl</c> without dragging WinAppSDK into
    /// Ghostty.Core. <c>PaneHost</c> sets this on construction;
    /// reads go through <c>LeafPaneExtensions.Terminal()</c> which
    /// concentrates the unchecked cast in exactly one place.
    ///
    /// Tests leave Tag null. PaneTree only needs reference equality
    /// and never dereferences Tag.
    /// </summary>
    public object? Tag { get; set; }

    /// <summary>
    /// Profile snapshot the leaf was created from. Null = legacy
    /// keyboard-Split path (<c>Alt+Shift+D</c>) or the very first leaf
    /// in a no-profiles-configured cold start.
    /// </summary>
    internal Ghostty.Core.Profiles.ProfileSnapshot? Snapshot { get; set; }
}

internal sealed class SplitPane : PaneNode
{
    public PaneOrientation Orientation { get; }
    public PaneNode Child1 { get; set; }
    public PaneNode Child2 { get; set; }
    public double Ratio { get; set; }

    public SplitPane(PaneOrientation o, PaneNode c1, PaneNode c2, double ratio = 0.5)
    {
        Orientation = o; Child1 = c1; Child2 = c2; Ratio = ratio;
    }
}

internal enum PaneOrientation { Vertical, Horizontal }
