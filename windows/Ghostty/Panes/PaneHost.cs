using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;
using Ghostty.Controls;
using Ghostty.Hosting;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Ghostty.Panes;

/// <summary>
/// UserControl that renders a <see cref="PaneNode"/> tree as nested
/// 2-cell <see cref="Grid"/>s with <see cref="Splitter"/>s between
/// children. Owns the tree root, the active leaf pointer, and the
/// operations that mutate them (split, close, directional focus).
///
/// Stable TerminalControl instances:
///   <see cref="TerminalControl"/> instances are created once when
///   their <see cref="LeafPane"/> is created, and are reused as the
///   tree is rebuilt: only their parent Grid changes. Recreating a
///   TerminalControl would tear down its libghostty surface and lose
///   the running shell, which is not what splitting a pane should do.
///
/// Tree mutations:
///   <see cref="Split"/> and <see cref="Close"/> mutate <see cref="_root"/>
///   via <see cref="PaneTree"/> and then call <see cref="Rebuild"/>.
///   The whole subtree is rebuilt for simplicity; the trees are tiny
///   and rebuild cost is negligible compared to a libghostty frame.
///
/// Focus tracking:
///   We subscribe to each <see cref="TerminalControl.GotFocus"/> and
///   maintain <see cref="ActiveLeaf"/>. <see cref="LeafFocused"/> fires
///   when ActiveLeaf changes so MainWindow can re-route the title.
/// </summary>
internal sealed partial class PaneHost : UserControl, IPaneHost
{
    // Not readonly: RehostTo writes this during cross-window tab
    // detach. UI-thread-only -- all reads and writes happen on the
    // dispatcher queue, so no synchronization is needed.
    private GhosttyHost _host;
    private readonly Func<ProfileSnapshot?, TerminalControl> _terminalFactory;

    // Pane highlight system, rendered as an overlay Canvas above the
    // split tree. Two layers of chrome:
    //
    //   - _activeBorderFrame: an accent-colored border tracking the
    //     active leaf's bounds. Positioned via TransformToVisual.
    //   - _dimRects: one semi-transparent dark rectangle per INACTIVE
    //     leaf, positioned over each inactive leaf's bounds. Gives
    //     the visual effect of the active pane being "brighter" than
    //     its siblings without touching the terminal contents.
    //
    // Above both sits _tabContentBorderFrame, which frames the tab's
    // whole terminal area rather than any one leaf. It is not part of the
    // overlay: it needs no positioning, and it stays up through a zoom,
    // which collapses the overlay.
    //
    // The two frames are free to overlap. Both are Borders on the same
    // brush at PaneChrome.ActiveBorderThickness, and layout rounding
    // snaps each to the same whole device pixels at full opacity, so a
    // second stroke over the first lands on pixels that are already that
    // colour. Measured on a live window at 96 DPI: a single-pane tab's
    // edge, where both frames draw, is the same 2px run of the same RGB
    // as the edge of a split tab where only one of them does.
    //
    // Doing highlights as an overlay (instead of per-leaf Borders
    // inside the split tree) avoids splitter occlusion: a splitter in
    // a parent Grid is not a sibling of a leaf's chrome and cannot
    // be defeated with Canvas.ZIndex. The overlay sits above
    // everything and is tracked via TransformToVisual on each layout.
    //
    // Because it is an overlay it reserves no layout space, so each
    // leaf holds its own terminal surface off its edges by
    // PaneChrome.SurfaceInset (applied inside TerminalControl, below
    // the leaf root, so the layout-slot chain this overlay walks is
    // unaffected). That gutter is what keeps this chrome from painting
    // over live cells.
    // Assigned once in BuildChrome() (called from every ctor), never
    // reassigned. Not readonly because BuildChrome is a shared helper
    // rather than the ctor body itself.
    private Canvas _highlightOverlay = null!;
    private Border _activeBorderFrame = null!;
    // Frames the whole tab's terminal area. Always present and always
    // touching the tab strip, which is what makes the selected tab's
    // folder shape joinable no matter how the tab is split -- see
    // BuildChrome.
    private Border _tabContentBorderFrame = null!;
    private readonly Dictionary<LeafPane, Rectangle> _dimRects = new();
    private FrameworkElement _treeRoot = null!; // assigned in ctor before use

    // Startup-glow appearance/enablement, pushed from MainWindow
    // (PaneHost has no config access of its own; this mirrors
    // SetActiveBorderBrush). Read when a leaf's surface spawns.
    private bool _startupGlowEnabled;
    private Windows.UI.Color _startupGlowTrail = Microsoft.UI.Colors.DodgerBlue;
    private Windows.UI.Color _startupGlowLead = Microsoft.UI.Colors.White;

    // One startup-glow lifecycle per leaf's TerminalControl, alive only from
    // surface spawn until the glow fades (or the pane closes). Three
    // dictionaries keyed by the same control, added and removed in lockstep
    // by TeardownGlow: the state machine (lifecycle), the composition
    // renderer (drawing), and the mount Canvas that hosts the renderer's
    // child visual in _highlightOverlay.
    private readonly Dictionary<TerminalControl, Core.Panes.PaneStartupGlowState> _glowStates = new();
    private readonly Dictionary<TerminalControl, PaneStartupGlow> _glows = new();
    private readonly Dictionary<TerminalControl, Canvas> _glowMounts = new();

    // Panes smaller than this in either dimension (DIPs) skip the glow.
    private const double MinGlowDimension = 80.0;
    // Fallback for surfaces that never reach first_render (a command that
    // produces no output still renders, so hitting this means something is
    // genuinely stuck, and a glow that never leaves is worse than none).
    private static readonly TimeSpan GlowCapDuration = TimeSpan.FromMilliseconds(10000);
    private static readonly TimeSpan GlowFadeDuration = TimeSpan.FromMilliseconds(250);

    // Top-right "restore" affordance shown only while a pane is zoomed,
    // styled like the quake pin button. Clicking it unzooms. The resting
    // glyph is a plain magnifier (status: this pane is magnified); on
    // hover it swaps to the zoom-out magnifier (action hint: click to
    // restore). Lives permanently in the host Grid above the floated
    // pane, Collapsed except during zoom.
    private Button _restoreZoomButton = null!;
    private FontIcon _restoreZoomIcon = null!;
    private const string RestoreZoomGlyphRest = "";  // Zoom (magnifier)
    private const string RestoreZoomGlyphHover = ""; // ZoomOut (magnifier minus)

    private static readonly Brush DefaultActiveBorderBrush =
        new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
    // Subtle dark film over inactive panes. ~22% black matches the
    // weight of VSCode's inactive editor group tint and is visible
    // against #0C0C0C terminal bg without washing out text.
    private static readonly Brush InactiveDimBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(56, 0, 0, 0));

    private PaneNode _root;
    private LeafPane _activeLeaf;
    // When non-null, the active leaf is "zoomed" — it fills the entire
    // host and the rest of the tree is hidden. Mirrors the
    // toggle_split_zoom keybind. Zoom keeps the split tree mounted but
    // Collapsed and floats the active leaf full-size above it; unzoom
    // splices the leaf back into its slot. We never rebuild the tree on
    // zoom toggle, so deep trees do not leave stale divider visuals.
    private LeafPane? _zoomedLeaf;
    // The split Grid and cell the zoomed leaf was detached from, so
    // unzoom can put it back without rebuilding. Valid only while
    // _zoomedLeaf is non-null.
    private Grid? _zoomRestoreParent;
    private int _zoomRestoreColumn;
    private int _zoomRestoreRow;
    // Set once the last leaf has been closed and the window is tearing
    // down. DisposeAllLeaves honors it so it does not walk a tree that
    // has already been disposed leaf-by-leaf in CloseLeaf.
    private bool _allLeavesClosed;

    // Undo/redo of structural pane ops. Per-PaneHost (per-tab). Snapshots
    // are captured before each op; closed leaves are retained alive here
    // until their entry is evicted (default ~5s, configurable via the
    // libghostty `undo-timeout` config), so undo resurrects the shell.
    //
    // _undoEnabled is false when `undo-timeout = 0` (upstream's "disable
    // undo" sentinel). It gates every capture/restore path: no op is
    // recorded, closes hard-tear-down their shell immediately (no soft-close
    // retention), the prune timer is never armed, and CanUndo/CanRedo report
    // false so the command palette hides the dead entries.
    private readonly bool _undoEnabled;
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly Core.Panes.PaneHistory _history;
    // Set while Undo()/Redo() restore state, so the capture helper does
    // not record the restore itself as a new undoable op.
    private bool _restoring;
    private System.Threading.ITimer? _pruneTimer;

    /// <summary>
    /// Raised when the active leaf changes (initially and on every focus
    /// change between leaves). Subscribers receive the new active leaf.
    /// </summary>
    public event EventHandler<LeafPane>? LeafFocused;

    /// <summary>
    /// Raised when the active leaf's <c>TerminalControl</c> reports a
    /// new OSC 9;4 state. Rewires across leaf-focus changes so only
    /// the currently active leaf drives the tab-level indicator.
    /// </summary>
    public event EventHandler<Ghostty.Core.Tabs.TabProgressState>? ProgressChanged;

    // The leaf whose terminal we are currently subscribed to for
    // progress updates. Swapped in BindActiveLeafProgress whenever
    // the active leaf changes.
    private TerminalControl? _progressBoundTerminal;

    private void BindActiveLeafProgress()
    {
        var next = _activeLeaf.Terminal();
        if (ReferenceEquals(next, _progressBoundTerminal)) return;
        _progressBoundTerminal?.ProgressChanged -= OnActiveLeafProgressChanged;
        _progressBoundTerminal = next;
        next.ProgressChanged += OnActiveLeafProgressChanged;
        // Re-emit the new leaf's last known state so subscribers see
        // a correct value immediately after a focus change — without
        // this the tab would stay stuck on the previous leaf's progress.
        ProgressChanged?.Invoke(this, next.CurrentProgress);
    }

    private void OnActiveLeafProgressChanged(object? sender, Ghostty.Core.Tabs.TabProgressState state)
        => ProgressChanged?.Invoke(this, state);

    /// <summary>
    /// Raised when the directory the tab should name changes: the active
    /// leaf's shell reported a new one, or a focus change handed the tab a
    /// different leaf. Mirrors <see cref="ProgressChanged"/> in scope --
    /// background panes record their own cwd but do not retitle the tab.
    /// Null means "this pane has not reported one".
    /// </summary>
    public event EventHandler<string?>? CwdChanged;

    // The cwd lives on the leaf, not the terminal, so a focus change needs
    // no resubscription -- only a re-emit of the newly active leaf's value.
    private void EmitActiveLeafCwd() => CwdChanged?.Invoke(this, _activeLeaf.LastCwd);

    /// <summary>Raised when the active leaf's terminal rings the bell,
    /// carrying the decoded bell-features. Rewired across leaf-focus
    /// changes, mirroring <see cref="ProgressChanged"/>.</summary>
    public event EventHandler<Ghostty.Core.Bell.BellFeatures>? BellRang;

    /// <summary>Raised when the active leaf acknowledges the bell (focus
    /// or keystroke), so the tab title indicator can clear.</summary>
    public event EventHandler? BellAcknowledged;

    // The leaf we are currently forwarding bell events from. Swapped in
    // BindActiveLeafBell when the active leaf changes. Unlike progress, we
    // do NOT re-emit on bind: a pane switch must not re-ring the tab.
    private TerminalControl? _bellBoundTerminal;

    private void BindActiveLeafBell()
    {
        var next = _activeLeaf.Terminal();
        if (ReferenceEquals(next, _bellBoundTerminal)) return;
        if (_bellBoundTerminal is not null)
        {
            _bellBoundTerminal.BellRang -= OnActiveLeafBellRang;
            _bellBoundTerminal.BellAcknowledged -= OnActiveLeafBellAcknowledged;
        }
        _bellBoundTerminal = next;
        next.BellRang += OnActiveLeafBellRang;
        next.BellAcknowledged += OnActiveLeafBellAcknowledged;
    }

    private void OnActiveLeafBellRang(object? sender, Ghostty.Core.Bell.BellFeatures features)
        => BellRang?.Invoke(this, features);

    private void OnActiveLeafBellAcknowledged(object? sender, EventArgs e)
        => BellAcknowledged?.Invoke(this, EventArgs.Empty);


    /// <summary>
    /// Raised when the last leaf in the tree closes. The owning
    /// <c>TabManager</c> subscribes and routes to
    /// <c>TabManager.CloseTab</c>; window-close then cascades via
    /// <c>LastTabClosed</c> when this was the last tab.
    /// </summary>
    public event EventHandler? LastLeafClosed;

    /// <summary>
    /// Raised when a leaf requests its pane context menu. Carries the
    /// originating <see cref="TerminalControl"/> and the pointer position in
    /// that control's coordinates (null when requested from the keyboard).
    /// MainWindow listens and shows the flyout (it owns the dispatch entry
    /// points the menu commands route through).
    /// </summary>
    public event EventHandler<PaneContextMenuRequest>? ContextMenuRequested;

    /// <summary>
    /// Currently focused leaf. Never null after construction; closing
    /// the last leaf raises <see cref="LastLeafClosed"/> instead of
    /// nulling this.
    /// </summary>
    public LeafPane ActiveLeaf => _activeLeaf;

    public PaneNode RootNode => _root;

    public LeafPane? ZoomedLeaf => _zoomedLeaf;

    // ---- test seam accessors (WINTTY_TEST_SEAM=1) --------------------
    // Named TestSeam* so the seam's footprint on this class is greppable
    // and removable as one shape. The rects read RENDERED geometry rather
    // than the model, because a harness asking "is the active border drawn
    // over the right-hand leaf" must not be answered by the same field it
    // is trying to corroborate.

    /// <summary>Leaf index in <see cref="PaneTree.Leaves"/> order (Child1 first).</summary>
    internal int TestSeamActiveLeafIndex
        => PaneTree.Leaves(_root).ToList().IndexOf(_activeLeaf);

    internal int TestSeamLeafCount => PaneTree.Leaves(_root).Count();

    /// <summary>Each leaf's bounds in window-root DIPs, in leaf order.</summary>
    internal IReadOnlyList<Rect> TestSeamLeafRects
        => PaneTree.Leaves(_root).Select(l => TestSeamRectOf(l.Terminal())).ToList();

    /// <summary>
    /// Where <see cref="PositionActiveBorderOverLeaf"/> actually put the
    /// stroke, in window-root DIPs; an empty rect while it is hidden.
    /// </summary>
    internal Rect TestSeamActiveBorderRect
        => _activeBorderFrame.Visibility == Visibility.Visible
            ? TestSeamRectOf(_activeBorderFrame)
            : default;

    /// <summary>The stroke's colour, so a capture harness knows what to look for.</summary>
    internal uint TestSeamActiveBorderArgb
        => _activeBorderFrame.BorderBrush is SolidColorBrush brush
            ? ((uint)brush.Color.A << 24) | ((uint)brush.Color.R << 16)
              | ((uint)brush.Color.G << 8) | brush.Color.B
            : 0u;

    /// <summary>
    /// Focus the Nth leaf through the same call the split-navigation chords
    /// make, so <see cref="OnTerminalGotFocus"/> -- not the seam -- is what
    /// moves <see cref="ActiveLeaf"/>.
    /// </summary>
    internal bool TestSeamFocusLeaf(int index)
    {
        var leaves = PaneTree.Leaves(_root).ToList();
        return index >= 0 && index < leaves.Count
            && leaves[index].Terminal().Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// Which leaf actually holds keyboard focus, or -1 when focus is
    /// outside this host. Walks up from <paramref name="focused"/> because
    /// focus settles on a child of the leaf's control as often as on the
    /// control itself.
    /// </summary>
    internal int TestSeamFocusedLeafIndex(DependencyObject? focused)
    {
        var leaves = PaneTree.Leaves(_root).ToList();
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            for (var i = 0; i < leaves.Count; i++)
                if (ReferenceEquals(node, leaves[i].Terminal())) return i;
        }
        return -1;
    }

    private static Rect TestSeamRectOf(FrameworkElement element)
    {
        var origin = element.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return new Rect(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
    }

    /// <summary>
    /// Raised after any structural change to the tree (split, close,
    /// zoom toggle, equalize, resize, undo/redo restore). The session
    /// manager coalesces this into a debounced persist.
    /// </summary>
    public event EventHandler? LayoutChanged;

    private void RaiseLayoutChanged() => LayoutChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Override the pane chrome color. Pass null to revert to the default
    /// DodgerBlue.
    /// </summary>
    /// <remarks>
    /// Both frames, from the one value. The tab frame is the line the
    /// selected tab's folder shape closes onto, so a per-tab preset that
    /// reached only the focus frame would show as a tab stroked in the
    /// preset meeting a pane framed in the cursor colour.
    /// </remarks>
    public void SetActiveBorderBrush(Brush? brush)
    {
        var resolved = brush ?? DefaultActiveBorderBrush;
        _activeBorderFrame.BorderBrush = resolved;
        _tabContentBorderFrame.BorderBrush = resolved;
    }

    /// <summary>Push startup-glow enablement and colors from MainWindow.
    /// Mirrors <see cref="SetActiveBorderBrush"/>: PaneHost holds no config,
    /// so the window hands down what a spawn needs. Affects future spawns
    /// only; a glow already running keeps its colors.</summary>
    public void SetStartupGlowConfig(bool enabled, Windows.UI.Color trail, Windows.UI.Color lead)
    {
        _startupGlowEnabled = enabled;
        _startupGlowTrail = trail;
        _startupGlowLead = lead;
    }

    /// <summary>
    /// Repaint every mounted leaf's chrome gutter for the current
    /// terminal background. Leaves repaint themselves when constructed
    /// and on every attach, so this only has to catch the ones already
    /// sitting in the tree when the config changes.
    /// </summary>
    public void RefreshGutterBrush()
    {
        // Once the last leaf is closed _root still references it, and
        // walking it here would touch a terminal that CloseLeaf has
        // already torn down. Same invariant DisposeAllLeaves honors.
        if (_allLeavesClosed) return;

        foreach (var leaf in Core.Panes.PaneTree.Leaves(_root))
        {
            // Per leaf, not around the loop: a config reload can land
            // while a window is tearing down or a tab is mid-detach, and
            // a XAML write against a dying leaf must not strand the rest
            // of the tree on the old fill. Letting it escape would be
            // worse still -- this runs from OnConfigReloadedChrome, which
            // is a sequence of deliberately disjoint chrome steps, so a
            // throw here strands every later step in that sequence and
            // leaves the window half-applied. The fan-out now contains
            // each subscriber, so the other windows keep their reload
            // either way, but this window's own remaining steps do not.
            try
            {
                leaf.Terminal().ApplyGutterBrush();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                    or InvalidOperationException
                                    or NullReferenceException)
            {
            }
        }
    }

    /// <summary>
    /// Number of leaves in the tree. Implemented via a tree walk; the
    /// trees are tiny (typically &lt;10 leaves) so this is cheap.
    /// </summary>
    public int PaneCount
    {
        get
        {
            int count = 0;
            CountLeaves(_root, ref count);
            return count;
        }
    }

    private static void CountLeaves(PaneNode node, ref int count)
    {
        if (node is LeafPane) { count++; return; }
        if (node is SplitPane sp) { CountLeaves(sp.Child1, ref count); CountLeaves(sp.Child2, ref count); }
    }

    /// <param name="host">Per-window libghostty host. Passed to every
    /// <see cref="TerminalControl"/> created by this PaneHost.</param>
    /// <param name="terminalFactory">Factory that produces a fresh
    /// <see cref="TerminalControl"/> with no <see cref="TerminalControl.Host"/>
    /// set. PaneHost assigns Host before adding the control to the
    /// visual tree, ensuring the OnLoaded guard fires only once Host
    /// is in place. Receives the profile snapshot (or null for the
    /// legacy no-profile path) so it can pre-set
    /// <see cref="TerminalControl.Snapshot"/> before the control loads.</param>
    /// <param name="initialSnapshot">Profile snapshot for the first leaf,
    /// or null for cold-start / legacy paths.</param>
    /// <param name="undoPolicy">Whether undo/redo is enabled and, if so, its
    /// per-operation eviction window — resolved from the libghostty
    /// <c>undo-timeout</c> config by <see cref="Ghostty.Tabs.PaneHostFactory"/>.
    /// Null falls back to <see cref="Core.Panes.UndoPolicy.Default"/> (enabled,
    /// 5s). A disabled policy (upstream's <c>undo-timeout = 0</c>) turns off
    /// every capture/restore path for this tab. The policy is captured once
    /// here, so a config reload only affects PaneHosts created afterward (new
    /// tabs) — existing tabs keep their construction-time policy since
    /// <see cref="Core.Panes.PaneHistory"/> holds its window immutably.</param>
    public PaneHost(GhosttyHost host, Func<ProfileSnapshot?, TerminalControl> terminalFactory,
        ProfileSnapshot? initialSnapshot = null, Core.Panes.UndoPolicy? undoPolicy = null)
    {
        _host = host;
        _terminalFactory = terminalFactory;
        var policy = undoPolicy ?? Core.Panes.UndoPolicy.Default;
        _undoEnabled = policy.Enabled;
        _history = new Core.Panes.PaneHistory(_time, policy.Window);

        BuildChrome();

        // Initial single leaf. Pass the initialSnapshot so the terminal
        // spawns with the right command/working-directory from OnLoaded.
        var firstTerminal = CreateTerminal(initialSnapshot);
        _activeLeaf = new LeafPane { Tag = firstTerminal, Snapshot = initialSnapshot };
        _root = _activeLeaf;

        // Two-layer host Grid: the actual split tree below, the
        // highlight overlay above. The overlay Canvas does not
        // capture pointer events (IsHitTestVisible=false), so the
        // tree below receives all input normally.
        var hostGrid = new Grid();
        _treeRoot = BuildVisual(_root);
        hostGrid.Children.Add(_treeRoot);
        hostGrid.Children.Add(_highlightOverlay);
        hostGrid.Children.Add(_tabContentBorderFrame);
        hostGrid.Children.Add(_restoreZoomButton);
        Content = hostGrid;

        WireCommonHandlers();
    }

    /// <summary>
    /// Restore-seeded ctor: adopt a pre-built tree instead of a single
    /// fresh leaf. Leaves must carry their <see cref="LeafPane.Snapshot"/>
    /// with <see cref="LeafPane.Tag"/> null; this ctor creates each leaf's
    /// TerminalControl via the same wiring as a live split, then rebuilds
    /// the visual and re-applies zoom exactly like Undo's RestoreFrom.
    /// <paramref name="activeLeaf"/> must be a leaf of <paramref name="root"/>;
    /// <paramref name="zoomedLeaf"/>, when non-null and present in the tree,
    /// is re-zoomed after the rebuild.
    /// </summary>
    public PaneHost(
        GhosttyHost host,
        Func<ProfileSnapshot?, TerminalControl> terminalFactory,
        PaneNode root,
        LeafPane activeLeaf,
        LeafPane? zoomedLeaf,
        Core.Panes.UndoPolicy? undoPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(activeLeaf);

        _host = host;
        _terminalFactory = terminalFactory;
        var policy = undoPolicy ?? Core.Panes.UndoPolicy.Default;
        _undoEnabled = policy.Enabled;
        _history = new Core.Panes.PaneHistory(_time, policy.Window);

        BuildChrome();

        // Materialize a TerminalControl for every restored leaf via the
        // same wiring a live split uses, so titles/progress/close-surface
        // callbacks route correctly. Leaves arrive with Tag null.
        foreach (var leaf in PaneTree.Leaves(root))
            leaf.Tag = CreateTerminal(leaf.Snapshot);

        _root = root;
        _activeLeaf = activeLeaf;

        var hostGrid = new Grid();
        _treeRoot = BuildVisual(_root);
        hostGrid.Children.Add(_treeRoot);
        hostGrid.Children.Add(_highlightOverlay);
        hostGrid.Children.Add(_tabContentBorderFrame);
        hostGrid.Children.Add(_restoreZoomButton);
        Content = hostGrid;

        // Re-enter zoom on the restored leaf via the existing enter-path,
        // mirroring RestoreFrom. Only if it is still present in the tree.
        // _restoring guards CaptureForUndo so the re-zoom is not itself
        // recorded as an undoable op (same as RestoreFrom).
        if (zoomedLeaf is not null && PaneTree.Leaves(_root).Contains(zoomedLeaf))
        {
            _restoring = true;
            try
            {
                _activeLeaf = zoomedLeaf;
                ToggleSplitZoom();
            }
            finally
            {
                _restoring = false;
            }
        }

        WireCommonHandlers();
    }

    /// <summary>
    /// Build the highlight overlay + restore-from-zoom button chrome.
    /// Shared by both constructors so the chrome is identical regardless
    /// of how the initial tree is seeded.
    /// </summary>
    private void BuildChrome()
    {
        var t = Core.Panes.PaneChrome.ActiveBorderThickness;
        _activeBorderFrame = new Border
        {
            BorderBrush = DefaultActiveBorderBrush,
            BorderThickness = new Thickness(t),
            Background = null,
            IsHitTestVisible = false,
        };
        _highlightOverlay = new Canvas
        {
            IsHitTestVisible = false,
        };
        _highlightOverlay.Children.Add(_activeBorderFrame);
        // Force the overlay above any sibling in the host Grid so
        // the chrome never gets composited under the terminal.
        Canvas.SetZIndex(_highlightOverlay, 999);
        // Inside the overlay, the active border sits above the startup-glow
        // mounts (998, set in OnLeafSurfaceSpawned) so a freshly spawned
        // pane glows just under its own focus stroke rather than over it.
        // Children with no explicit ZIndex (the dim rects) stay at 0, below
        // both, which is the order they already drew in.
        Canvas.SetZIndex(_activeBorderFrame, 999);

        // The tab's own frame, around the whole terminal area rather than
        // around a leaf. The selected tab is drawn as a folder joined to
        // this line, so the line has to be there wherever the focus is:
        // with only the active leaf framed, the join existed by
        // coincidence -- a single pane fills its tab, so its top edge
        // happened to land under the strip. Split the tab, focus a pane
        // away from the top edge, and the tab joined to nothing while the
        // seam cover rubbed out a border that was not there.
        //
        // Stretched in the host Grid rather than placed on the overlay
        // Canvas: it needs no coordinates of its own, so it cannot drift
        // from the area it frames, and it survives a zoom, which collapses
        // the overlay.
        _tabContentBorderFrame = new Border
        {
            BorderBrush = DefaultActiveBorderBrush,
            BorderThickness = new Thickness(t),
            Background = null,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        // Over the overlay, not under it. The inactive-pane dim film lives
        // there and runs to those panes' outer edges, which are this
        // frame's edges too -- beneath it the tab frame comes out visibly
        // darkened for exactly the stretch an unfocused pane occupies.
        Canvas.SetZIndex(_tabContentBorderFrame, 1000);

        // Restore-from-zoom affordance. Mirrors the quake pin button:
        // transparent, borderless, top-right, 32x28. Hidden until a pane
        // is zoomed (ToggleSplitZoom shows it). Sits above the floated
        // pane (ZIndex over the overlay) so it stays clickable.
        _restoreZoomIcon = new FontIcon
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
            Glyph = RestoreZoomGlyphRest,
            FontSize = 14,
        };
        _restoreZoomButton = new Button
        {
            Content = _restoreZoomIcon,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 8, 0),
            Width = 32,
            Height = 28,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed,
        };
        ToolTipService.SetToolTip(_restoreZoomButton, "Zoomed in — click to restore");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_restoreZoomButton, "Restore zoomed pane");
        // One above the tab frame, keeping the "topmost chrome" it had
        // before that frame existed. Its margin clears the stroke today,
        // so this is about the ordering staying deliberate rather than
        // about a pixel currently in dispute.
        Canvas.SetZIndex(_restoreZoomButton, 1001);
        // These handlers capture `this`, but the button is owned solely by
        // this PaneHost (a child of its host Grid), so the reference is an
        // internal cycle that dies with the PaneHost - not a leak across the
        // GhosttyHost boundary, so unlike the public events it needs no
        // explicit teardown.
        _restoreZoomButton.Click += (_, _) => ToggleSplitZoom();
        // Resting glyph signals "zoomed"; hover previews the zoom-out action.
        _restoreZoomButton.PointerEntered += (_, _) => _restoreZoomIcon.Glyph = RestoreZoomGlyphHover;
        _restoreZoomButton.PointerExited += (_, _) => _restoreZoomIcon.Glyph = RestoreZoomGlyphRest;
    }

    /// <summary>
    /// Wire the layout/focus/prune handlers after Content is set. Shared
    /// by both constructors.
    /// </summary>
    private void WireCommonHandlers()
    {
        // Reposition the highlight whenever layout settles. Cheap;
        // single TransformToVisual + four set-property calls. Covers
        // window resize, splitter drag, and the post-Split layout
        // pass that finally exposes new-leaf bounds.
        LayoutUpdated += (_, _) => UpdateHighlightPosition();

        // Defer the first LeafFocused so subscribers (MainWindow) can
        // wire up before the event fires.
        Loaded += (_, _) =>
        {
            BindActiveLeafProgress();
            BindActiveLeafBell();
            LeafFocused?.Invoke(this, _activeLeaf);
            // Evict expired undo entries ~once a second; dispose any shell
            // that is no longer reachable from the live tree or history.
            // Skip entirely when undo is disabled — nothing is ever captured,
            // so there is nothing to prune and no need to run a timer.
            if (_undoEnabled)
                _pruneTimer ??= _time.CreateTimer(
                    _ => DispatcherQueue.TryEnqueue(PruneHistory),
                    null,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1));
        };
        // Rebind progress and bell whenever the active leaf changes later,
        // and hand the tab the newly active leaf's directory.
        LeafFocused += (_, _) =>
        {
            BindActiveLeafProgress();
            BindActiveLeafBell();
            EmitActiveLeafCwd();
        };
    }

    // Public operations -------------------------------------------------

    /// <summary>
    /// Split the active leaf with the given orientation. The new leaf
    /// becomes the active leaf. Inherits the active leaf's profile
    /// snapshot so a split of a pwsh tab does not spawn cmd.exe.
    /// </summary>
    public void Split(PaneOrientation orientation)
        => Split(orientation, snapshot: _activeLeaf.Snapshot);

    /// <summary>
    /// Split the active leaf with the given orientation. The new leaf
    /// becomes the active leaf. <paramref name="snapshot"/> (when
    /// non-null) is stored on the new <see cref="LeafPane"/>.
    /// </summary>
    public void Split(PaneOrientation orientation, ProfileSnapshot? snapshot)
    {
        // Capture BEFORE the implicit unzoom below so undo restores the
        // pre-split zoom state too.
        CaptureForUndo(Core.Panes.PaneOpKind.Split);

        // Unzoom before splitting so the new sub-Grid is inserted into
        // the full tree, not into the zoomed single-leaf visual.
        if (_zoomedLeaf is not null)
        {
            _zoomedLeaf = null;
            Rebuild();
        }

        var oldActive = _activeLeaf;
        var wasRoot = ReferenceEquals(_root, oldActive);
        var newTerminal = CreateTerminal(snapshot);
        var newLeaf = new LeafPane { Tag = newTerminal };
        newLeaf.Snapshot = snapshot;
        _root = PaneTree.Split(_root, oldActive, newLeaf, orientation);
        _activeLeaf = newLeaf;

        // INCREMENTAL rebuild: do NOT rebuild the entire tree from
        // scratch. A full rebuild detaches every existing leaf from
        // its current Grid and re-attaches it to a freshly built
        // Grid in the same call stack, which trips a "child still
        // has a parent" COMException in WinUI 3 once the tree gets
        // more than two levels deep.
        //
        // Instead, find the leaf's current Grid parent, remove just
        // that one leaf from its slot, build a small sub-Grid for
        // the new SplitPane (which contains oldActive + newLeaf),
        // and put the sub-Grid in the slot the old leaf occupied.
        // Every other leaf in the window stays in its place,
        // completely untouched.
        //
        // Special case: if oldActive WAS the root (single-pane window
        // before this split), there is no Grid parent. Fall back to
        // a full rebuild via the existing path - the single-pane to
        // two-pane case has no nested tree and works fine that way.
        SplitPane? newSubSplit = wasRoot
            ? (SplitPane)_root
            : PaneTree.FindParent(_root, oldActive)?.Parent;

        if (newSubSplit is null || wasRoot || oldActive.Terminal().Parent is not Grid currentParent)
        {
            // Root replacement (oldActive was the entire content of
            // PaneHost), or some unexpected state. Full rebuild handles
            // both cases correctly because there is no nested visual
            // tree to confuse WinUI 3's parent tracking.
            //
            // wasRoot is checked explicitly: the single root leaf IS a
            // direct child of the host Grid, so the Parent-is-not-Grid
            // guard below never catches it. Without this the first split
            // would splice in place but leave _treeRoot pointing at the
            // old leaf, so every later Rebuild / ApplyAllRatios / zoom
            // operates on the wrong element (stale dividers, dead resize).
            Rebuild();
        }
        else
        {
            // In-place replacement of the single old-active leaf with
            // a fresh sub-Grid for the new SplitPane.
            int col = Grid.GetColumn(oldActive.Terminal());
            int row = Grid.GetRow(oldActive.Terminal());
            currentParent.Children.Remove(oldActive.Terminal());
            var subGrid = (Grid)BuildVisual(newSubSplit);
            Grid.SetColumn(subGrid, col);
            Grid.SetRow(subGrid, row);
            currentParent.Children.Add(subGrid);
        }

        // Defer the highlight + focus until layout settles. The new
        // leaf has zero ActualWidth/Height at this exact instant
        // because the framework has not measured it yet, so a sync
        // UpdateHighlightPosition would Collapse the overlay rect.
        DispatcherQueue.TryEnqueue(() =>
        {
            newTerminal.Focus(FocusState.Programmatic);
            UpdateHighlightPosition();
        });

        RaiseLayoutChanged();
    }

    /// <summary>
    /// Close the active leaf. If it was the only leaf, raises
    /// <see cref="LastLeafClosed"/>; otherwise the sibling subtree
    /// replaces the parent split and focus moves to the sibling's
    /// first leaf.
    /// </summary>
    public void CloseActive()
    {
        CloseLeaf(_activeLeaf, undoable: true);
    }

    /// <summary>
    /// Switch every <see cref="TerminalControl"/> leaf in this tree to
    /// report to <paramref name="newHost"/>. Called by
    /// <see cref="MainWindow.DetachTabToNewWindow"/> after the PaneHost
    /// has been removed from the old window's visual parent and before
    /// it is added to the new window's. UI thread only.
    ///
    /// Per-leaf Detach-then-Adopt moves each surface handle between
    /// the two hosts' per-window <c>_surfaces</c> dictionaries AND
    /// rewrites the process-wide <c>_hostBySurface</c> routing map so
    /// libghostty callbacks post-move reach the destination host. The
    /// spec accepts the one-update-lost race (Risk 3): a callback
    /// arriving between Detach and Adopt for the same handle looks up,
    /// misses, drops. An async progress state resyncs on the next
    /// OSC 9;4.
    /// </summary>
    internal void RehostTo(GhosttyHost newHost)
    {
        foreach (var leaf in PaneTree.Leaves(_root))
        {
            var terminal = leaf.Terminal();
            var surface = new Interop.GhosttySurface(terminal.SurfaceHandle);
            _host.Detach(surface);
            newHost.Adopt(surface, terminal);
            terminal.Host = newHost;
        }
        _host = newHost;
    }

    /// <summary>
    /// Tell libghostty, per leaf, whether this tab's pixels reach the
    /// screen. A hidden tab stops presenting and releases its GPU atlas
    /// copies until it is shown again (the renderer rebuilds them lazily
    /// on the next frame); occlusion is a surface property, so a split tab
    /// flips every leaf. Keyed on visibility, never on focus: a background
    /// tab that never gains focus must still come back rendering.
    /// </summary>
    internal void SetSurfaceVisibility(bool visible)
    {
        foreach (var leaf in PaneTree.Leaves(_root))
        {
            var handle = leaf.Terminal().SurfaceHandle;
            // Zero before the surface exists (the control has not Loaded
            // yet - SwapActivePane first runs during construction, ahead
            // of every surface) or after it is disposed; either way the
            // native call would dereference a null surface. A surface
            // that does not exist yet renders nothing and will get its
            // real state from the next swap.
            if (handle == IntPtr.Zero) continue;
            Interop.NativeMethods.SurfaceSetVisible(
                new Interop.GhosttySurface(handle), visible);
        }
    }

    /// <summary>
    /// The newest activity stamp across every leaf's surface, for the
    /// idle tracker's sweep. Computed on demand: the trees are tiny and
    /// the sweep runs every 30 seconds, so a maintained cached value
    /// would be write traffic for nothing.
    /// </summary>
    public long LastActivityTick
    {
        get
        {
            // Same teardown guard the other tree walks use: after the
            // last leaf closes, _root still references it and the walk
            // would read a terminal CloseLeaf has already torn down.
            if (_allLeavesClosed) return 0;
            long latest = 0;
            foreach (var leaf in PaneTree.Leaves(_root))
            {
                var tick = leaf.Terminal().LastActivityTick;
                if (tick > latest) latest = tick;
            }
            return latest;
        }
    }

    /// <summary>
    /// Tear down every leaf's libghostty surface. Called by
    /// <see cref="MainWindow"/> when the window is closing, since
    /// surface lifetime is decoupled from <c>Unloaded</c> events and
    /// the framework's natural teardown does not free them.
    /// </summary>
    public void DisposeAllLeaves()
    {
        // Tear down any in-flight startup glows first so their timers stop
        // and their composition visuals are released promptly. Glows are
        // keyed by TerminalControl independent of tree state, so this runs
        // regardless of the _allLeavesClosed gate below. Snapshot the keys
        // first: TeardownGlow mutates the dictionaries it is iterating.
        foreach (var terminal in _glowStates.Keys.ToList())
            TeardownGlow(terminal);

        // Tree walk is gated on _allLeavesClosed: every leaf was
        // already disposed one-by-one as the tree collapsed; _root
        // still references the last-closed leaf but walking it here
        // would double-dispose (DisposeSurface is idempotent, but the
        // walk is wasted work and a trap for the next reader).
        if (!_allLeavesClosed)
        {
            foreach (var leaf in PaneTree.Leaves(_root))
            {
                leaf.Terminal().DisposeSurface();
            }
        }

        // Tear down any shell still retained ONLY by the undo history
        // (soft-closed panes that were never evicted). Stop the timer
        // first so no prune races this teardown. Guard on the live tree
        // so we never double-dispose a leaf the walk above already freed.
        _pruneTimer?.Dispose();
        _pruneTimer = null;
        var liveLeaves = Core.Panes.PaneTree.Leaves(_root).ToHashSet();
        foreach (var leaf in _history.Clear())
        {
            if (!liveLeaves.Contains(leaf)) TeardownLeaf(leaf);
        }

        // Event-nulling intentionally runs unconditionally: the gate
        // above only controls whether we re-walk the tree, not whether
        // this PaneHost is going away. Both code paths (tree-collapsed
        // and not) reach here as part of tab teardown, so subscribers
        // must always be dropped. Matches TerminalControl.DisposeSurface.
        LeafFocused = null;
        ProgressChanged = null;
        BellRang = null;
        BellAcknowledged = null;
        LastLeafClosed = null;
    }

    /// <summary>
    /// Close a specific leaf. Used both by the keybinding (which closes
    /// <see cref="ActiveLeaf"/>) and by <see cref="TerminalControl.CloseRequested"/>
    /// from libghostty's close-surface callback.
    /// </summary>
    public void CloseLeaf(LeafPane leaf) => CloseLeaf(leaf, undoable: false);

    /// <param name="undoable">When true and at least one pane survives,
    /// the close is recorded for undo and the leaf's surface is NOT torn
    /// down — it lingers (running) until the undo entry is evicted, so
    /// undo can resurrect the live shell. Shell-initiated closes pass
    /// false (a dead shell is not worth resurrecting).</param>
    public void CloseLeaf(LeafPane leaf, bool undoable)
    {
        // A retained (already soft-closed) leaf is no longer in the live
        // tree; if its lingering shell exits and fires CloseRequested,
        // ignore it — eviction will dispose it.
        if (!Core.Panes.PaneTree.Leaves(_root).Contains(leaf)) return;

        // Which leaf (if any) was zoomed before this close. Used at the
        // end to keep an unrelated background close (e.g. a pane's shell
        // exiting on its own) from yanking the user out of zoom.
        var zoomedBefore = _zoomedLeaf;

        // Decide undoability now: only meaningful if a pane survives.
        // PaneTree.Close is a pure model op (no visual side effects), so
        // computing it up front to drive the teardown decision is safe.
        var newRoot = PaneTree.Close(_root, leaf);
        // Soft-close (retain the live shell for undo) only when undo is
        // enabled. With undo off, a surviving-pane close hard-tears-down the
        // shell immediately, matching upstream: a disabled undo timeout means
        // closed surfaces don't linger in the background.
        var softClose = undoable && newRoot is not null && _undoEnabled;
        if (softClose)
        {
            // Snapshot the tree WITH the leaf still present (and its shell
            // alive) so undo can resurrect it. The history entry retains
            // the leaf; teardown is deferred to eviction.
            CaptureForUndo(Core.Panes.PaneOpKind.Close);

            // The retained shell keeps the surface alive, but the leaf still
            // leaves the visual tree below, so a pane closed before its first
            // render would otherwise keep its glow orbiting a frozen rectangle
            // until the cap. Close the state here: Idle raises StateChanged,
            // which enqueues the same TeardownGlow a fade ending would, and
            // that stops the animation and lifts the mount off the overlay.
            if (_glowStates.TryGetValue(leaf.Terminal(), out var closingGlow))
                closingGlow.Close();
        }
        else
        {
            // Hard close: free the shell now (last pane, or shell-exit).
            TeardownLeaf(leaf);
        }

        // Capture the leaf's visual parent BEFORE detaching. This is
        // the Grid that visualizes the PaneTree split about to collapse;
        // we reuse it as the in-place splice point for the surviving
        // sibling visual instead of rebuilding the whole tree. See the
        // incremental-close branch below.
        var leafParentGrid = leaf.Terminal().Parent as Grid;

        // Detach the closed terminal from its visual parent Grid so the
        // old split Grid does not hold a reference that keeps the WinUI
        // compositor rendering a ghost DXGI swap chain surface. A
        // soft-closed leaf must leave the visual tree too; its surface
        // keeps compositing into nothing until restored or evicted.
        DetachFromParent(leaf.Terminal());

        if (newRoot is null)
        {
            // Last leaf - flag the host so DisposeAllLeaves on window
            // close skips iterating a tree whose only node is already
            // disposed, and tell TabManager to close this tab. Window
            // close then cascades through TabManager.LastTabClosed to
            // MainWindow.Close when this was the only tab.
            _allLeavesClosed = true;
            LastLeafClosed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _root = newRoot;
        // Clear zoom if the zoomed leaf was closed or if only one leaf
        // remains (zoom is meaningless on a single pane). Reset the whole
        // zoom state locally rather than relying on the downstream Rebuild
        // so the restore button and slot can never be left stranded.
        if (_zoomedLeaf is not null
            && (ReferenceEquals(_zoomedLeaf, leaf) || _root is LeafPane))
        {
            _zoomedLeaf = null;
            _zoomRestoreParent = null;
            HideRestoreZoomButton();
        }

        // Focus the first leaf of the (former) sibling subtree. We
        // pick the parent's sibling first so the focus stays close to
        // where the closed pane was.
        var nextActive = PaneTree.FirstLeaf(newRoot);
        _activeLeaf = nextActive;

        // INCREMENTAL rebuild: splice the surviving sibling visual into
        // the collapsed parent Grid's former slot instead of tearing
        // down and rebuilding the whole visual tree. A full Rebuild
        // works for 2-pane trees but regresses to ghost visuals once
        // the tree is 3+ levels deep - the same WinUI 3 "child already
        // has a parent" / stale-DCOMP-visual behavior that Split
        // mitigates via its non-root incremental path. Falls back to a
        // full Rebuild for the root-replacement case where there is no
        // nested visual structure to confuse the framework.
        if (!TryIncrementalCloseRebuild(leafParentGrid)) Rebuild();
        UpdateHighlightPosition();
        DispatcherQueue.TryEnqueue(() => nextActive.Terminal().Focus(FocusState.Programmatic));

        // A close while zoomed always force-unzooms (the structural rebuild
        // clears zoom state). If the pane that closed was NOT the zoomed
        // one and the zoomed pane is still alive, re-enter zoom on it so an
        // unrelated background close does not disrupt the user's view.
        if (zoomedBefore is not null
            && !ReferenceEquals(zoomedBefore, leaf)
            && _zoomedLeaf is null
            && PaneCount > 1
            && PaneTree.Leaves(_root).Any(l => ReferenceEquals(l, zoomedBefore)))
        {
            _activeLeaf = zoomedBefore;
            ToggleSplitZoom();
        }

        // The active leaf was reassigned above, before focus lands on it,
        // so OnTerminalGotFocus sees a leaf that is already active and
        // raises nothing. Raise it here: the tab's title, directory,
        // progress and bell all rebind on this event, and without it the
        // tab kept naming the pane that just closed until the survivor's
        // next prompt.
        LeafFocused?.Invoke(this, _activeLeaf);
        RaiseLayoutChanged();
    }

    /// <summary>
    /// Final teardown of a leaf: unsubscribe its TerminalControl from
    /// focus/close tracking and free its libghostty surface. Idempotent
    /// (DisposeSurface is). Split out so an undoable (soft) close can
    /// DEFER this until the undo entry is evicted, keeping the shell
    /// alive so undo can resurrect it.
    /// </summary>
    private void TeardownLeaf(LeafPane leaf)
    {
        var t = leaf.Terminal();
        t.GotFocus -= OnTerminalGotFocus;
        t.CloseRequested -= OnTerminalCloseRequested;
        t.ContextMenuRequested -= OnTerminalContextMenuRequested;
        t.PwdChanged -= OnTerminalPwdChanged;
        // Tear down the per-surface startup glow so its controller, renderer
        // and mount do not outlive the disposed terminal. Runs before
        // DisposeSurface so a glow timer that fires mid-teardown finds its
        // dictionaries already empty instead of driving a visual whose mount
        // is coming off the tree.
        TeardownGlow(t);
        t.DisposeSurface();
    }

    // Startup glow -------------------------------------------------------

    private void OnLeafSurfaceSpawned(object? sender, EventArgs e)
    {
        if (sender is not TerminalControl terminal) return;
        if (!_startupGlowEnabled) return;
        // One glow per control. SurfaceSpawned fires once per control, so
        // this is only a guard against a future second raise landing here.
        if (_glowStates.ContainsKey(terminal)) return;

        var leaf = LeafForTerminal(PaneTree.Leaves(_root), terminal);
        if (leaf is null) return;

        // Bounds may not be settled at spawn time (SurfaceSpawned fires from
        // OnLoaded, before first layout). Create the mount and renderer at
        // zero size now and let PositionGlowMount size them; the layout
        // handler keeps them tracked until the glow ends.
        var mount = new Canvas { IsHitTestVisible = false, Width = 0, Height = 0 };
        // Just under the active border (999, set in BuildChrome).
        Canvas.SetZIndex(mount, 998);
        _highlightOverlay.Children.Add(mount);
        _glowMounts[terminal] = mount;

        var glow = new PaneStartupGlow(mount, new Vector2(0f, 0f),
            _startupGlowTrail, _startupGlowLead);
        _glows[terminal] = glow;

        var state = new Core.Panes.PaneStartupGlowState(
            new Ghostty.Core.Config.SystemSchedulerTimer(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    Ghostty.Core.Config.SystemSchedulerTimer>.Instance),
            cap: GlowCapDuration,
            fade: GlowFadeDuration);
        // The timer callback runs on a threadpool thread; the visual tree
        // only moves on the dispatcher thread.
        state.StateChanged += phase => DispatcherQueue.TryEnqueue(() => OnGlowPhase(terminal, phase));
        _glowStates[terminal] = state;

        state.Start();
        glow.StartGlow();

        // Size now if layout is already available (splitting an
        // already-laid-out window); otherwise the layout handler catches it.
        PositionGlowMount(terminal, mount, leaf);
    }

    private void OnLeafFirstRender(object? sender, EventArgs e)
    {
        if (sender is not TerminalControl terminal) return;
        // The pane produced renderable content for the first time: end this
        // leaf's glow now rather than waiting out the cap. NotifyReady is a
        // no-op if the glow already faded or never started, so a late or
        // duplicate signal is harmless. FirstRender is raised on the UI
        // thread, and the state machine is thread-safe regardless.
        if (_glowStates.TryGetValue(terminal, out var state))
            state.NotifyReady();
    }

    private void OnGlowPhase(TerminalControl terminal, Core.Panes.PaneStartupGlowState.Phase phase)
    {
        if (!_glows.TryGetValue(terminal, out var glow)) return;
        switch (phase)
        {
            case Core.Panes.PaneStartupGlowState.Phase.FadingOut:
                glow.BeginFadeOut(GlowFadeDuration);
                break;
            case Core.Panes.PaneStartupGlowState.Phase.Idle:
                TeardownGlow(terminal);
                break;
        }
    }

    private void TeardownGlow(TerminalControl terminal)
    {
        if (_glowStates.Remove(terminal, out var state)) state.Dispose();
        if (_glows.Remove(terminal, out var glow)) glow.Dispose();
        if (_glowMounts.Remove(terminal, out var mount)) _highlightOverlay.Children.Remove(mount);
    }

    // Position+size one glow mount over its leaf, enforcing the minimum-size
    // degradation. Called per-spawn and from the layout handler, both of which
    // already resolved the leaf; the layout pass walks the tree once for every
    // overlay layer, so a lookup here would be a second traversal per mount
    // per tick. When the leaf is too small or not yet laid out, the mount is
    // collapsed to zero size so the glow is simply invisible (and reappears if
    // the pane later grows).
    private void PositionGlowMount(TerminalControl terminal, Canvas mount, LeafPane? leaf)
    {
        if (leaf is null) return;

        var ctl = leaf.Terminal();
        // Same "not laid out yet" guard the other overlay layers use.
        if (ctl.ActualWidth <= 0 || ctl.ActualHeight <= 0)
        {
            CollapseGlowMount(terminal, mount);
            return;
        }

        // LeafLayoutBounds walks layout slots rather than transforms: at cold
        // start the render-thread transform is not committed by the idle
        // compositor for ~750ms, so a TransformToVisual-based mount would
        // strand at zero size until then (see LeafLayoutBounds).
        var bounds = LeafLayoutBounds(ctl);
        if (bounds.Width < MinGlowDimension || bounds.Height < MinGlowDimension)
        {
            CollapseGlowMount(terminal, mount);
            return;
        }

        Canvas.SetLeft(mount, bounds.X);
        Canvas.SetTop(mount, bounds.Y);
        mount.Width = bounds.Width;
        mount.Height = bounds.Height;
        if (_glows.TryGetValue(terminal, out var glow))
            glow.UpdateSize(new Vector2((float)bounds.Width, (float)bounds.Height));
    }

    // The one leaf of these whose control is this terminal, matched by
    // reference rather than by any key the leaf does not have.
    private static LeafPane? LeafForTerminal(IEnumerable<LeafPane> leaves, TerminalControl terminal)
    {
        foreach (var leaf in leaves)
        {
            if (ReferenceEquals(leaf.Terminal(), terminal)) return leaf;
        }

        return null;
    }

    private void CollapseGlowMount(TerminalControl terminal, Canvas mount)
    {
        mount.Width = 0;
        mount.Height = 0;
        if (_glows.TryGetValue(terminal, out var glow))
            glow.UpdateSize(new Vector2(0f, 0f));
    }

    /// <summary>
    /// Replace the Grid that visualized the now-collapsed parent split
    /// with the surviving sibling visual, in place. Mirrors the
    /// incremental splice Split uses on its non-root path. Returns
    /// false when the caller must fall back to <see cref="Rebuild"/>
    /// (root replacement, zoomed, or any unexpected state).
    /// </summary>
    private bool TryIncrementalCloseRebuild(Grid? leafParentGrid)
    {
        // Zoom hides everything but the active leaf, so the visual
        // parent chain does not mirror the tree. Full Rebuild is the
        // only sane path: it rewires _treeRoot from scratch against
        // the unzoomed state the caller already restored.
        if (_zoomedLeaf is not null) return false;

        // leafParentGrid is null when the closed leaf was the sole
        // child of PaneHost.Content (i.e. _treeRoot was the leaf's
        // TerminalControl itself). Only happens on root-replacement
        // paths, which full Rebuild handles correctly.
        if (leafParentGrid is null) return false;

        // Find the sibling visual: whatever non-splitter child remains
        // in leafParentGrid after the closing leaf was detached.
        FrameworkElement? siblingVisual = null;
        foreach (var ch in leafParentGrid.Children)
        {
            if (ch is Splitter) continue;
            if (ch is FrameworkElement fe) { siblingVisual = fe; break; }
        }
        if (siblingVisual is null) return false;

        // Splice in place. Two cases:
        //   1. leafParentGrid IS the _treeRoot - sibling becomes the
        //      new _treeRoot inside hostGrid.
        //   2. leafParentGrid is nested inside another Grid - sibling
        //      takes leafParentGrid's row/column slot in that
        //      grandparent.
        // In both cases we ClearVisualTree the collapsed leafParentGrid
        // so the compositor drops every reference to the now-dead
        // split Grid and its splitter, otherwise WinUI 3 leaves ghost
        // DCOMP visuals on screen.
        if (ReferenceEquals(leafParentGrid, _treeRoot))
        {
            if (Content is not Grid hostGrid) return false;
            leafParentGrid.Children.Remove(siblingVisual);
            ClearVisualTree(leafParentGrid);
            hostGrid.Children.Remove(leafParentGrid);
            _treeRoot = siblingVisual;
            hostGrid.Children.Insert(0, _treeRoot);
            _highlightOverlay.Visibility = Visibility.Visible;
            return true;
        }

        if (leafParentGrid.Parent is not Grid grandparentGrid) return false;

        int col = Grid.GetColumn(leafParentGrid);
        int row = Grid.GetRow(leafParentGrid);
        leafParentGrid.Children.Remove(siblingVisual);
        ClearVisualTree(leafParentGrid);
        grandparentGrid.Children.Remove(leafParentGrid);
        Grid.SetColumn(siblingVisual, col);
        Grid.SetRow(siblingVisual, row);
        grandparentGrid.Children.Add(siblingVisual);
        return true;
    }

    /// <summary>
    /// Reset every split ratio to 0.5, giving all panes equal space.
    /// Mirrors upstream's <c>equalize_splits</c> keybind.
    /// </summary>
    public void EqualizeSplits()
    {
        // Only record undo when there is a split to equalize; on a single
        // leaf Equalize is a no-op, so capturing would push a useless entry
        // (and needlessly clear redo).
        if (_root is SplitPane) CaptureForUndo(Core.Panes.PaneOpKind.Equalize);
        PaneTree.Equalize(_root);
        RaiseLayoutChanged();
        // When zoomed, only update the model - unzoom re-applies every
        // ratio to the live tree when the user toggles back.
        if (_zoomedLeaf is not null) return;
        // Structure is unchanged - only ratios. Apply them to the
        // existing Grids in place (the mouse-drag mechanism) instead of
        // Rebuild(): a full rebuild recreates every Splitter and leaves
        // stale divider visuals on deep trees. In-place keeps focus too,
        // so no focus-restore is needed.
        ApplyAllRatios();
        UpdateHighlightPosition();
    }

    /// <summary>
    /// Toggle zoom on the active leaf. When zoomed, the active leaf
    /// fills the entire host and the rest of the tree is hidden. When
    /// unzoomed, the tree visual is restored. No-op on a single leaf.
    /// Mirrors upstream's <c>toggle_split_zoom</c> keybind.
    /// </summary>
    public void ToggleSplitZoom()
    {
        if (PaneCount <= 1) return;

        if (Content is not Grid hostGrid) return;

        // Record the pre-toggle zoom state. No-op while restoring so the
        // re-zoom that RestoreFrom performs is not itself recorded.
        CaptureForUndo(Core.Panes.PaneOpKind.Zoom);
        RaiseLayoutChanged();

        if (_zoomedLeaf is not null)
        {
            // Unzoom: splice the floated leaf back into the slot it came
            // from and slide the still-mounted tree back into view. No
            // Rebuild(), so no stale divider visuals. If the restore slot
            // was lost (should not happen), fall back to a full rebuild.
            var zoomed = _zoomedLeaf;
            _zoomedLeaf = null;
            HideRestoreZoomButton();
            if (_zoomRestoreParent is null)
            {
                Rebuild();
                UpdateHighlightPosition();
                DispatcherQueue.TryEnqueue(() => _activeLeaf.Terminal().Focus(FocusState.Programmatic));
                return;
            }

            var leafCtl = zoomed.Terminal();
            DetachFromParent(leafCtl); // remove from the host's float slot
            Grid.SetColumn(leafCtl, _zoomRestoreColumn);
            Grid.SetRow(leafCtl, _zoomRestoreRow);
            _zoomRestoreParent.Children.Add(leafCtl);
            _zoomRestoreParent = null;
            ParkTree(park: false); // translate the tree back to its resting spot
            // A ratio change while zoomed (EqualizeSplits) only touched
            // the model; sync the live Grids now that they are shown.
            ApplyAllRatios();
            // Force a synchronous measure+arrange so the just-respliced leaf
            // has valid layout slots (and a measured size) before we position
            // the chrome below. Without it the leaf is unmeasured here and the
            // border/dim only settle on a coalesced layout pass up to ~750ms
            // later -- the visible unzoom delay. (The unpark Translation
            // itself commits lazily on the idle compositor, but
            // PositionOverlayOverLeaf reads layout slots, not that transform,
            // so the chrome no longer waits on the compositor.)
            UpdateLayout();
            _highlightOverlay.Visibility = Visibility.Visible;
            UpdateHighlightPosition();
            DispatcherQueue.TryEnqueue(() => _activeLeaf.Terminal().Focus(FocusState.Programmatic));
        }
        else
        {
            // Zoom: keep the tree mounted but parked OFF-SCREEN, and float
            // the active leaf full-size above it. We park via an
            // ancestor-visual Translation rather than Visibility.Collapsed
            // because collapsing does NOT stop a SwapChainPanel's DX12
            // chain from compositing (the other panes would stay on
            // screen) - but a Translation moves the bound swap chain with
            // it. The tree is never torn down, so unzoom needs no
            // ghost-prone reconstruction.
            _zoomedLeaf = _activeLeaf;
            var leafCtl = _activeLeaf.Terminal();
            _zoomRestoreParent = leafCtl.Parent as Grid;
            _zoomRestoreColumn = Grid.GetColumn(leafCtl);
            _zoomRestoreRow = Grid.GetRow(leafCtl);
            DetachFromParent(leafCtl);
            ParkTree(park: true);
            // hostGrid has no row/column defs, so a child at cell 0,0 fills
            // it. The z-ordered overlay (collapsed here) stays on top.
            Grid.SetColumn(leafCtl, 0);
            Grid.SetRow(leafCtl, 0);
            hostGrid.Children.Insert(1, leafCtl);
            _highlightOverlay.Visibility = Visibility.Collapsed;
            // Surface the restore affordance over the now-full-size pane.
            _restoreZoomIcon.Glyph = RestoreZoomGlyphRest;
            _restoreZoomButton.Visibility = Visibility.Visible;
            DispatcherQueue.TryEnqueue(() => leafCtl.Focus(FocusState.Programmatic));
        }
    }

    /// <summary>True when there is at least one undoable op on the stack.
    /// Lets the command palette omit a dead "Undo" entry. Mirrors the
    /// concrete-only surfacing of <see cref="Undo"/> (the router casts to
    /// <see cref="PaneHost"/> rather than going through IPaneHost).
    /// Always false when undo is disabled (the history is never populated,
    /// but the explicit guard keeps the intent obvious).</summary>
    public bool CanUndo => _undoEnabled && _history.CanUndo;

    /// <summary>Mirror of <see cref="CanUndo"/> for the redo stack.</summary>
    public bool CanRedo => _undoEnabled && _history.CanRedo;

    /// <summary>
    /// Restore the model to the state before the most recent undoable
    /// op. No-op if the history is empty. Resurrects a soft-closed pane's
    /// live shell. Mirrors upstream's time-bounded undo.
    /// </summary>
    public void Undo()
    {
        if (!_undoEnabled) return;
        RestoreFrom(_history.Undo(Snapshot(Core.Panes.PaneOpKind.Split)));
    }

    /// <summary>Re-apply the most recently undone op.</summary>
    public void Redo()
    {
        if (!_undoEnabled) return;
        RestoreFrom(_history.Redo(Snapshot(Core.Panes.PaneOpKind.Split)));
    }

    // Common restore path for Undo/Redo. The OpKind on the snapshot we
    // hand the history is irrelevant (it is only used for coalescing on
    // Push), so Split is passed as a harmless placeholder above.
    private void RestoreFrom(Core.Panes.PaneSnapshot? snapshot)
    {
        if (snapshot is null) return;

        _restoring = true;
        try
        {
            _root = snapshot.Root;
            _activeLeaf = snapshot.Active;
            _zoomedLeaf = null;        // Rebuild() clears zoom; re-enter below
            Rebuild();                 // full visual rebuild from the restored tree

            if (snapshot.Zoomed is not null
                && Core.Panes.PaneTree.Leaves(_root).Contains(snapshot.Zoomed))
            {
                _activeLeaf = snapshot.Zoomed;
                ToggleSplitZoom();     // re-enter zoom via the existing enter-path
            }

            UpdateHighlightPosition();
            LeafFocused?.Invoke(this, _activeLeaf);
            var target = _activeLeaf;
            DispatcherQueue.TryEnqueue(() => target.Terminal().Focus(FocusState.Programmatic));
        }
        finally
        {
            _restoring = false;
        }

        RaiseLayoutChanged();
    }

    // Hide the restore-from-zoom button and reset its glyph to the
    // resting magnifier. Called on every unzoom path, including the
    // force-unzoom that falls back to Rebuild from Split / Close.
    private void HideRestoreZoomButton()
    {
        _restoreZoomButton.Visibility = Visibility.Collapsed;
        _restoreZoomIcon.Glyph = RestoreZoomGlyphRest;
    }

    /// <summary>
    /// Slide the whole split tree off the bottom of the host (park) or
    /// back to rest (unpark) using the element's Composition Translation
    /// facade. Used by zoom to hide the non-zoomed panes without
    /// unmounting them: Translation moves a SwapChainPanel-bound DX12
    /// chain with no swap-chain resize (the same facade the quake-terminal
    /// slide uses), whereas Visibility.Collapsed leaves the chain
    /// compositing. Keeping the tree mounted means unzoom restores it
    /// without a reconstruction that would leave stale divider visuals.
    /// </summary>
    private void ParkTree(bool park)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(_treeRoot, true);
        var visual = ElementCompositionPreview.GetElementVisual(_treeRoot);
        if (park)
        {
            // Translate down by the tree's own height so it sits entirely
            // below the visible host bounds (clipped away by the window).
            // Fall back to a large constant if it has not been measured.
            var dy = (float)_treeRoot.ActualHeight;
            if (dy <= 0) dy = 100_000f;
            visual.Properties.InsertVector3("Translation", new Vector3(0f, dy, 0f));
        }
        else
        {
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
    }

    /// <summary>
    /// Whether the active leaf is currently zoomed to fill the host.
    /// </summary>
    public bool IsZoomed => _zoomedLeaf is not null;

    /// <summary>
    /// Move focus to the leaf nearest the active leaf in the requested
    /// direction. Geometric (uses rendered rects), not tree-order.
    /// No-op if no leaf lies in that direction.
    /// </summary>
    public void FocusDirection(FocusDirection direction)
    {
        // No-op while zoomed -- only one leaf is visible.
        if (_zoomedLeaf is not null) return;

        var allLeaves = PaneTree.Leaves(_root).ToList();
        if (allLeaves.Count <= 1) return;

        var activeRect = GetLeafRect(_activeLeaf);
        if (activeRect is null) return;

        LeafPane? best = null;
        double bestDistance = double.MaxValue;

        var ac = Center(activeRect.Value);
        foreach (var leaf in allLeaves)
        {
            if (ReferenceEquals(leaf, _activeLeaf)) continue;
            var rect = GetLeafRect(leaf);
            if (rect is null) continue;
            var c = Center(rect.Value);

            // Direction filter: candidate's center must lie strictly in
            // the requested direction from the active center, AND must
            // overlap on the perpendicular axis (so a pane two rows
            // down isn't considered "Right" of the active one just
            // because its center.X is greater).
            switch (direction)
            {
                case Panes.FocusDirection.Left:
                    if (c.X >= ac.X) continue;
                    if (rect.Value.Bottom <= activeRect.Value.Top) continue;
                    if (rect.Value.Top >= activeRect.Value.Bottom) continue;
                    break;
                case Panes.FocusDirection.Right:
                    if (c.X <= ac.X) continue;
                    if (rect.Value.Bottom <= activeRect.Value.Top) continue;
                    if (rect.Value.Top >= activeRect.Value.Bottom) continue;
                    break;
                case Panes.FocusDirection.Up:
                    if (c.Y >= ac.Y) continue;
                    if (rect.Value.Right <= activeRect.Value.Left) continue;
                    if (rect.Value.Left >= activeRect.Value.Right) continue;
                    break;
                case Panes.FocusDirection.Down:
                    if (c.Y <= ac.Y) continue;
                    if (rect.Value.Right <= activeRect.Value.Left) continue;
                    if (rect.Value.Left >= activeRect.Value.Right) continue;
                    break;
            }

            // Manhattan distance between centers.
            var dist = Math.Abs(c.X - ac.X) + Math.Abs(c.Y - ac.Y);
            if (dist < bestDistance)
            {
                bestDistance = dist;
                best = leaf;
            }
        }

        best?.Terminal().Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// Move focus to the next leaf in left-to-right depth-first
    /// tree-order, wrapping back to the first leaf from the last.
    /// Complements the spatial <see cref="FocusDirection"/>: this one
    /// always advances even when no leaf lies in any spatial direction,
    /// matching Ghostty's <c>goto_split:next</c> binding.
    /// No-op while zoomed or with a single leaf.
    /// </summary>
    public void GotoNextSplit()
    {
        if (_zoomedLeaf is not null) return;
        var target = PaneTree.NextLeafInOrder(_root, _activeLeaf);
        target?.Terminal().Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// Mirror of <see cref="GotoNextSplit"/> walking in reverse.
    /// Maps to Ghostty's <c>goto_split:previous</c> binding.
    /// </summary>
    public void GotoPreviousSplit()
    {
        if (_zoomedLeaf is not null) return;
        var target = PaneTree.PreviousLeafInOrder(_root, _activeLeaf);
        target?.Terminal().Focus(FocusState.Keyboard);
    }

    // Default ratio delta per resize_split chord. 0.05 = 5% of the
    // split per press, giving the user 18-19 presses to slide the
    // divider from edge to edge. Tuned by feel; configurable later.
    private const double ResizeSplitDelta = 0.05;

    /// <summary>
    /// Move the divider closest to the active leaf in the requested
    /// direction. No-op while zoomed, with a single leaf, or when
    /// the active leaf has no ancestor split of the matching
    /// orientation (e.g. resize Up but all ancestors are vertical).
    /// Maps to Ghostty's <c>resize_split:DIRECTION</c> binding.
    /// </summary>
    public void ResizeSplit(ResizeDirection direction)
    {
        if (_zoomedLeaf is not null) return;
        if (PaneTree.Leaves(_root).Take(2).Count() <= 1) return;

        // Snapshot the pre-resize ratios (clone is independent of the live
        // tree) but only RECORD it if the resize actually moves a divider,
        // so a no-op resize (no matching-orientation ancestor) adds no
        // undo entry. Push goes through PaneHistory directly so coalescing
        // still collapses a held-chord burst into one undo step.
        var pre = (_restoring || !_undoEnabled) ? null : Snapshot(Core.Panes.PaneOpKind.Resize);
        if (!PaneTree.ResizeSplit(_root, _activeLeaf, direction, ResizeSplitDelta)) return;
        if (pre is not null) DisposeOrphans(_history.Push(pre));
        // Ratio-only change: apply in place (see EqualizeSplits) rather
        // than Rebuild(), which ghosts dividers on deep trees.
        ApplyAllRatios();
        UpdateHighlightPosition();
        RaiseLayoutChanged();
    }

    // Internals ---------------------------------------------------------

    private TerminalControl CreateTerminal(ProfileSnapshot? snapshot)
    {
        var t = _terminalFactory(snapshot);
        // Host MUST be set before the control is loaded; TerminalControl
        // throws otherwise.
        t.Host = _host;
        t.GotFocus += OnTerminalGotFocus;
        // CloseRequested fires from libghostty's close-surface callback
        // (via GhosttyHost). Route it to the leaf-level close path so
        // multi-pane closing collapses correctly.
        t.CloseRequested += OnTerminalCloseRequested;
        t.ContextMenuRequested += OnTerminalContextMenuRequested;
        t.PwdChanged += OnTerminalPwdChanged;
        // Startup glow: begin the orbit when this leaf's surface spawns; end
        // it on the FIRST of first_render (the pane produced renderable
        // content, shell-agnostic) or the cap timer as the fallback. We use
        // first_render rather than OSC 133 prompt-ready: prompt-ready depends
        // on shell integration loading and varies per shell
        // (cmd/pwsh/wsl/...), whereas first_render is universal.
        t.SurfaceSpawned += OnLeafSurfaceSpawned;
        t.FirstRender += OnLeafFirstRender;
        return t;
    }

    private void OnTerminalPwdChanged(object? sender, string? pwd)
    {
        if (sender is not TerminalControl tc) return;
        var leaf = PaneTree.Leaves(_root).FirstOrDefault(l => ReferenceEquals(l.Terminal(), tc));
        if (leaf is null) return;
        leaf.LastCwd = pwd;
        if (ReferenceEquals(leaf, _activeLeaf)) EmitActiveLeafCwd();
    }

    private void OnTerminalContextMenuRequested(object? sender, Windows.Foundation.Point? position)
    {
        if (sender is not TerminalControl tc) return;
        ContextMenuRequested?.Invoke(this, new PaneContextMenuRequest(tc, position));
    }

    private void OnTerminalGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TerminalControl tc) return;
        var leaf = PaneTree.Leaves(_root).FirstOrDefault(l => ReferenceEquals(l.Terminal(), tc));
        if (leaf is null) return;
        if (ReferenceEquals(leaf, _activeLeaf)) return;
        _activeLeaf = leaf;
        UpdateHighlightPosition();
        LeafFocused?.Invoke(this, _activeLeaf);
    }

    /// <summary>
    /// Reposition the highlight rectangle over the active leaf's
    /// rendered bounds. Called from <see cref="LayoutUpdated"/> and
    /// after structural changes (Split / Close). If the active leaf
    /// has not laid out yet (zero size), the rect is hidden until the
    /// next LayoutUpdated tick.
    /// </summary>
    /// <summary>
    /// Reposition the active-border rect over the active leaf, and
    /// reposition each inactive-dim rect over its corresponding leaf.
    /// Rects with zero bounds are Collapsed (hidden); laid-out ones
    /// are made Visible.
    /// </summary>
    private void UpdateHighlightPosition()
    {
        // Active border.
        PositionActiveBorderOverLeaf(_activeLeaf);

        // Dim rects: walk every current leaf. Active leaf's dim rect
        // (if any) is hidden; every other leaf gets its dim rect
        // positioned over its bounds. Leaves that no longer exist
        // in the tree get their dim rects pruned.
        var currentLeaves = PaneTree.Leaves(_root).ToHashSet();

        // Prune stale entries (leaves that were closed).
        var stale = _dimRects.Keys.Where(k => !currentLeaves.Contains(k)).ToList();
        foreach (var leaf in stale)
        {
            _highlightOverlay.Children.Remove(_dimRects[leaf]);
            _dimRects.Remove(leaf);
        }

        // Ensure every inactive leaf has a dim rect and position it.
        foreach (var leaf in currentLeaves)
        {
            if (ReferenceEquals(leaf, _activeLeaf))
            {
                if (_dimRects.TryGetValue(leaf, out var active))
                    active.Visibility = Visibility.Collapsed;
                continue;
            }

            if (!_dimRects.TryGetValue(leaf, out var dim))
            {
                dim = new Rectangle
                {
                    Fill = InactiveDimBrush,
                    IsHitTestVisible = false,
                };
                _dimRects[leaf] = dim;
                // Insert BEFORE the active-border rect so the border
                // still draws on top of its neighbor's dim film.
                _highlightOverlay.Children.Insert(0, dim);
            }

            PositionOverlayOverLeaf(dim, leaf, insetForStroke: false);
        }

        // Keep each live startup-glow mount tracked over its leaf while the
        // glow lasts. Cheap when the dictionary is empty (the common case
        // once every pane has rendered): the loop simply does not run, and
        // each mount takes its leaf from the walk above rather than sending
        // the tree through Leaves() again.
        foreach (var (terminal, mount) in _glowMounts)
            PositionGlowMount(terminal, mount, LeafForTerminal(currentLeaves, terminal));
    }

    private void PositionActiveBorderOverLeaf(LeafPane leaf)
    {
        var ctl = leaf.Terminal();
        if (ctl.ActualWidth <= 0 || ctl.ActualHeight <= 0)
        {
            _activeBorderFrame.Visibility = Visibility.Collapsed;
            return;
        }

        var bounds = LeafLayoutBounds(ctl);
        var t = Core.Panes.PaneChrome.ActiveBorderThickness;
        _activeBorderFrame.BorderThickness = new Thickness(t);
        Canvas.SetLeft(_activeBorderFrame, bounds.X);
        Canvas.SetTop(_activeBorderFrame, bounds.Y);
        _activeBorderFrame.Width = bounds.Width;
        _activeBorderFrame.Height = bounds.Height;
        _activeBorderFrame.Visibility = Visibility.Visible;
    }

    private static Rect LeafLayoutBounds(FrameworkElement ctl)
    {
        double bx = 0, by = 0;
        for (FrameworkElement? fe = ctl; fe is not null;
             fe = VisualTreeHelper.GetParent(fe) as FrameworkElement)
        {
            if (fe is PaneHost) break;
            var slot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(fe);
            bx += slot.X;
            by += slot.Y;
        }

        return new Rect(bx, by, ctl.ActualWidth, ctl.ActualHeight);
    }

    private void PositionOverlayOverLeaf(Rectangle rect, LeafPane leaf, bool insetForStroke)
    {
        var ctl = leaf.Terminal();
        if (ctl.ActualWidth <= 0 || ctl.ActualHeight <= 0)
        {
            rect.Visibility = Visibility.Collapsed;
            return;
        }

        var bounds = LeafLayoutBounds(ctl);
        // For the stroked active border, inset by half the stroke
        // thickness so the stroke draws entirely INSIDE the leaf bounds
        // (and so within the gutter each leaf reserves for it -- see
        // PaneChrome). For dim fills, use the full rect.
        var inset = insetForStroke ? Core.Panes.PaneChrome.ActiveBorderThickness / 2 : 0.0;
        Canvas.SetLeft(rect, bounds.X + inset);
        Canvas.SetTop(rect, bounds.Y + inset);
        rect.Width = Math.Max(0, bounds.Width - inset * 2);
        rect.Height = Math.Max(0, bounds.Height - inset * 2);
        rect.Visibility = Visibility.Visible;
    }

    private void OnTerminalCloseRequested(object? sender, EventArgs e)
    {
        if (sender is not TerminalControl tc) return;
        var leaf = PaneTree.Leaves(_root).FirstOrDefault(l => ReferenceEquals(l.Terminal(), tc));
        if (leaf is null) return;
        CloseLeaf(leaf);
    }

    // Build a snapshot of the CURRENT model. Root is a structural clone
    // (shared leaf identities) so later in-place ratio edits cannot
    // corrupt it. Caller supplies the op kind.
    private Core.Panes.PaneSnapshot Snapshot(Core.Panes.PaneOpKind kind)
        => new(Core.Panes.PaneTree.Clone(_root), _activeLeaf, _zoomedLeaf, kind);

    // Record the pre-op state for undo. No-op while restoring (so Undo's
    // own re-zoom/rebuild does not push history). Pushing clears the redo
    // stack, which can orphan a pane whose surface is still alive (its
    // only reference was a redo entry); tear those down so the shell does
    // not leak.
    private void CaptureForUndo(Core.Panes.PaneOpKind kind)
    {
        if (_restoring || !_undoEnabled) return;
        DisposeOrphans(_history.Push(Snapshot(kind)));
    }

    // Drop expired entries and dispose any leaf they orphaned that is no
    // longer in the live tree. Runs on the UI thread (timer marshals).
    private void PruneHistory()
    {
        // A prune may already be queued on the dispatcher when the host
        // tears down (DisposeAllLeaves disposes the timer + clears history).
        // It is harmless post-Clear, but bail explicitly so a future change
        // to Prune/Clear semantics can't turn this into a use-after-teardown.
        if (_allLeavesClosed) return;
        DisposeOrphans(_history.Prune(_time.GetUtcNow()));
    }

    // Tear down the libghostty surface of each orphaned leaf that is not
    // still in the live tree. Shared by the capture (redo-clear orphans),
    // prune (time-eviction orphans), and teardown paths so the
    // "never dispose a leaf still on screen" guard lives in one place.
    private void DisposeOrphans(IReadOnlyList<LeafPane> orphans)
    {
        if (orphans.Count == 0) return;
        var live = Core.Panes.PaneTree.Leaves(_root).ToHashSet();
        foreach (var leaf in orphans)
        {
            if (live.Contains(leaf)) continue; // still on screen — keep alive
            TeardownLeaf(leaf);
        }
    }

    private void Rebuild()
    {
        // Swap the tree visual inside the existing host Grid so the
        // overlay Canvas (the second child) stays on top across
        // rebuilds. Only used for the root-replacement case in
        // Split (and for Close); incremental splits mutate the
        // visual tree directly without a full rebuild.
        if (Content is not Grid hostGrid) return;

        // Rebuild always reconstructs the full tree from _root, so any
        // zoom is necessarily off afterward. Clearing the zoom state here
        // keeps the force-unzoom paths (Split / Close fall back to
        // Rebuild) consistent without each having to reset it.
        _zoomedLeaf = null;
        _zoomRestoreParent = null;
        HideRestoreZoomButton();

        // Clear old tree children recursively before removal so the
        // compositor drops all references to stale swap chain panels.
        // Without this, removed Grids that still contain child elements
        // can leave ghost visuals on screen.
        ClearVisualTree(_treeRoot);

        hostGrid.Children.Remove(_treeRoot);
        _treeRoot = BuildVisual(_root);
        hostGrid.Children.Insert(0, _treeRoot);
        // Restore the highlight overlay after unzoom (zoom hides it).
        _highlightOverlay.Visibility = Visibility.Visible;
    }

    private FrameworkElement BuildVisual(PaneNode node)
    {
        if (node is LeafPane leaf)
        {
            // The leaf's TerminalControl is stable across rebuilds.
            // Detach it from any previous parent before re-parenting.
            DetachFromParent(leaf.Terminal());
            return leaf.Terminal();
        }

        var split = (SplitPane)node;
        var grid = new Grid();
        if (split.Orientation == PaneOrientation.Vertical)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(split.Ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - split.Ratio, GridUnitType.Star) });

            var left = BuildVisual(split.Child1);
            Grid.SetColumn(left, 0);
            var right = BuildVisual(split.Child2);
            Grid.SetColumn(right, 1);

            grid.Children.Add(left);
            grid.Children.Add(right);

            // Splitter sits at column 0's right edge (the column boundary).
            // Pinned inside cell 0 with HorizontalAlignment.Right so it
            // rides the boundary as star weights change. No ColumnSpan.
            ApplyRatio(grid, split);
            var splitter = new Splitter(split, () => ApplyRatio(grid, split));
            Grid.SetColumn(splitter, 0);
            splitter.HorizontalAlignment = HorizontalAlignment.Right;
            splitter.VerticalAlignment = VerticalAlignment.Stretch;
            splitter.Width = Core.Panes.PaneChrome.DividerThickness;
            // Keep the splitter above the panes regardless of later child
            // order. Operations that re-add a leaf via Children.Add -- unzoom
            // (ToggleSplitZoom) splicing the floated leaf back, and an in-place
            // Split of a cell-0 leaf -- would otherwise append the leaf on top
            // of this splitter, occluding the 1px divider line and stealing its
            // drag hit-testing. A high z-index pins it on top by intent, not by
            // add-order luck.
            Canvas.SetZIndex(splitter, 1);
            grid.Children.Add(splitter);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(split.Ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - split.Ratio, GridUnitType.Star) });

            var top = BuildVisual(split.Child1);
            Grid.SetRow(top, 0);
            var bottom = BuildVisual(split.Child2);
            Grid.SetRow(bottom, 1);

            grid.Children.Add(top);
            grid.Children.Add(bottom);

            // Splitter sits at row 0's bottom edge (the row boundary).
            // Pinned inside cell 0 with VerticalAlignment.Bottom so it
            // rides the boundary as star weights change. No RowSpan.
            ApplyRatio(grid, split);
            var splitter = new Splitter(split, () => ApplyRatio(grid, split));
            Grid.SetRow(splitter, 0);
            splitter.VerticalAlignment = VerticalAlignment.Bottom;
            splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            splitter.Height = Core.Panes.PaneChrome.DividerThickness;
            // Pin above the panes so a later Children.Add (unzoom / cell-0
            // split) can't occlude the divider. See the vertical case above.
            Canvas.SetZIndex(splitter, 1);
            grid.Children.Add(splitter);
        }

        return grid;
    }

    /// <summary>
    /// Re-apply every split's ratio to its existing Grid, walking the
    /// live visual tree. The keyboard resize and equalize paths use this
    /// instead of <see cref="Rebuild"/>: only ratios changed, the tree
    /// structure is identical, so updating the existing
    /// ColumnDefinitions / RowDefinitions in place (exactly what the
    /// mouse-drag <see cref="Splitter"/> does) avoids recreating any
    /// Splitter. A full Rebuild recreates them and leaves stale divider
    /// DCOMP visuals on screen once the tree is 3+ levels deep - the same
    /// ghost-visual behavior Split / Close sidestep via incremental
    /// splicing.
    /// </summary>
    private void ApplyAllRatios() => ApplyRatiosRecursive(_treeRoot);

    private static void ApplyRatiosRecursive(FrameworkElement? visual)
    {
        // A Splitter is itself a Grid but hosts no split children; never
        // recurse into it or mistake it for a split Grid. A leaf is a
        // TerminalControl (not a Grid), which ends the recursion.
        if (visual is Splitter) return;
        if (visual is not Grid grid) return;

        // Each split Grid built by BuildVisual carries exactly one
        // Splitter child whose Split identifies the ratio it represents.
        // Child order is irrelevant (incremental Split can reorder
        // children), so we scan rather than index.
        Splitter? splitter = null;
        foreach (var child in grid.Children)
        {
            if (child is Splitter s) splitter = s;
            else if (child is FrameworkElement fe) ApplyRatiosRecursive(fe);
        }
        if (splitter is not null) ApplyRatio(grid, splitter.Split);
    }

    /// <summary>
    /// Apply the current ratio to the grid's row/column definitions
    /// without rebuilding any children. Called both on initial build
    /// and on each splitter drag delta.
    /// </summary>
    private static void ApplyRatio(Grid grid, SplitPane split)
    {
        if (split.Orientation == PaneOrientation.Vertical)
        {
            if (grid.ColumnDefinitions.Count == 2)
            {
                grid.ColumnDefinitions[0].Width = new GridLength(split.Ratio, GridUnitType.Star);
                grid.ColumnDefinitions[1].Width = new GridLength(1 - split.Ratio, GridUnitType.Star);
            }
        }
        else
        {
            if (grid.RowDefinitions.Count == 2)
            {
                grid.RowDefinitions[0].Height = new GridLength(split.Ratio, GridUnitType.Star);
                grid.RowDefinitions[1].Height = new GridLength(1 - split.Ratio, GridUnitType.Star);
            }
        }
    }

    /// <summary>
    /// Recursively clear all children from a visual subtree. This breaks
    /// compositor references to stale DXGI swap chain panels so removed
    /// Grids do not leave ghost visuals on screen. Surviving
    /// TerminalControls are re-parented by <see cref="BuildVisual"/>
    /// immediately after this runs.
    /// </summary>
    private static void ClearVisualTree(FrameworkElement element)
    {
        if (element is not Panel panel) return;
        for (var i = panel.Children.Count - 1; i >= 0; i--)
        {
            if (panel.Children[i] is FrameworkElement child)
                ClearVisualTree(child);
        }
        panel.Children.Clear();
    }

    private static void DetachFromParent(FrameworkElement child)
    {
        // A UIElement can only have one parent. Before reparenting a
        // stable TerminalControl into a freshly built Grid, we have to
        // explicitly remove it from wherever it currently lives. In
        // practice there is only one parent shape: PaneHost.Content is
        // a host Grid, every leaf lives in some Grid's Children below
        // it. The ContentControl fallback stays as a defense-in-depth
        // guard in case future wrapping ever puts a leaf directly in a
        // ContentControl.Content slot.
        switch (child.Parent)
        {
            case Panel panel:
                panel.Children.Remove(child);
                break;
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, child):
                contentControl.Content = null;
                break;
        }
    }

    private Rect? GetLeafRect(LeafPane leaf)
    {
        var ctl = leaf.Terminal();
        if (ctl.ActualWidth <= 0 || ctl.ActualHeight <= 0) return null;
        try
        {
            var transform = ctl.TransformToVisual(this);
            return transform.TransformBounds(new Rect(0, 0, ctl.ActualWidth, ctl.ActualHeight));
        }
        catch
        {
            return null;
        }
    }

    private static Point Center(Rect r) => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
}

/// <summary>
/// Direction for <see cref="PaneHost.FocusDirection(FocusDirection)"/>.
/// </summary>
internal enum FocusDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// Payload for <see cref="PaneHost.ContextMenuRequested"/>: which surface and
/// where (null position = keyboard-invoked).
/// </summary>
public readonly record struct PaneContextMenuRequest(
    Ghostty.Controls.TerminalControl Control,
    Windows.Foundation.Point? Position);
