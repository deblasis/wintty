using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Panes;
using Ghostty.Controls;
using Ghostty.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
internal sealed class PaneHost : UserControl, IPaneHost
{
    private readonly GhosttyHost _host;
    private readonly Func<TerminalControl> _terminalFactory;

    // Pane highlight system, rendered as an overlay Canvas above the
    // split tree. Two layers of chrome:
    //
    //   - _activeBorderRect: a 1.5px accent-colored border tracking
    //     the active leaf's bounds. Positioned via TransformToVisual.
    //   - _dimRects: one semi-transparent dark rectangle per INACTIVE
    //     leaf, positioned over each inactive leaf's bounds. Gives
    //     the visual effect of the active pane being "brighter" than
    //     its siblings without touching the terminal contents.
    //
    // Doing highlights as an overlay (instead of per-leaf Borders
    // inside the split tree) avoids splitter occlusion: a splitter in
    // a parent Grid is not a sibling of a leaf's chrome and cannot
    // be defeated with Canvas.ZIndex. The overlay sits above
    // everything and is tracked via TransformToVisual on each layout.
    private readonly Canvas _highlightOverlay;
    private readonly Rectangle _activeBorderRect;
    private readonly Dictionary<LeafPane, Rectangle> _dimRects = new();
    private FrameworkElement _treeRoot = null!; // assigned in ctor before use

    // TODO: route through a theme resource once the config layer
    // exists so a light theme does not end up with a dark film over
    // inactive panes and an accent-agnostic border over the active one.
    private static readonly Brush ActiveBorderBrush =
        new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
    // Subtle dark film over inactive panes. ~22% black matches the
    // weight of VSCode's inactive editor group tint and is visible
    // against #0C0C0C terminal bg without washing out text.
    private static readonly Brush InactiveDimBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(56, 0, 0, 0));

    private PaneNode _root;
    private LeafPane _activeLeaf;
    // Set once the last leaf has been closed and the window is tearing
    // down. DisposeAllLeaves honors it so it does not walk a tree that
    // has already been disposed leaf-by-leaf in CloseLeaf.
    private bool _allLeavesClosed;

    /// <summary>
    /// Raised when the active leaf changes (initially and on every focus
    /// change between leaves). Subscribers receive the new active leaf.
    /// </summary>
    public event EventHandler<LeafPane>? LeafFocused;

    /// <summary>
    /// Raised when the last leaf in the tree closes. Subscribers should
    /// close the window.
    /// </summary>
    public event EventHandler? LastLeafClosed;

    /// <summary>
    /// Currently focused leaf. Never null after construction; closing
    /// the last leaf raises <see cref="LastLeafClosed"/> instead of
    /// nulling this.
    /// </summary>
    public LeafPane ActiveLeaf => _activeLeaf;

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
    /// is in place.</param>
    public PaneHost(GhosttyHost host, Func<TerminalControl> terminalFactory)
    {
        _host = host;
        _terminalFactory = terminalFactory;

        _activeBorderRect = new Rectangle
        {
            Stroke = ActiveBorderBrush,
            StrokeThickness = 1.5,
            Fill = null,
            IsHitTestVisible = false,
        };
        _highlightOverlay = new Canvas
        {
            IsHitTestVisible = false,
        };
        _highlightOverlay.Children.Add(_activeBorderRect);
        // Force the overlay above any sibling in the host Grid so
        // the chrome never gets composited under the terminal.
        Canvas.SetZIndex(_highlightOverlay, 999);

        // Initial single leaf.
        var firstTerminal = CreateTerminal();
        _activeLeaf = new LeafPane { Tag = firstTerminal };
        _root = _activeLeaf;

        // Two-layer host Grid: the actual split tree below, the
        // highlight overlay above. The overlay Canvas does not
        // capture pointer events (IsHitTestVisible=false), so the
        // tree below receives all input normally.
        var hostGrid = new Grid();
        _treeRoot = BuildVisual(_root);
        hostGrid.Children.Add(_treeRoot);
        hostGrid.Children.Add(_highlightOverlay);
        Content = hostGrid;

        // Reposition the highlight whenever layout settles. Cheap;
        // single TransformToVisual + four set-property calls. Covers
        // window resize, splitter drag, and the post-Split layout
        // pass that finally exposes new-leaf bounds.
        //
        // TODO: LayoutUpdated fires aggressively (per arrange pass of
        // any descendant). At <20 leaves this is imperceptible, but
        // if ETW traces ever flag it, debounce via a DispatcherQueueTimer.
        LayoutUpdated += (_, _) => UpdateHighlightPosition();

        // Defer the first LeafFocused so subscribers (MainWindow) can
        // wire up before the event fires.
        Loaded += (_, _) => LeafFocused?.Invoke(this, _activeLeaf);
    }

    // Public operations -------------------------------------------------

    /// <summary>
    /// Split the active leaf with the given orientation. The new leaf
    /// becomes the active leaf.
    /// </summary>
    public void Split(PaneOrientation orientation)
    {
        var oldActive = _activeLeaf;
        var wasRoot = ReferenceEquals(_root, oldActive);
        var newTerminal = CreateTerminal();
        var newLeaf = new LeafPane { Tag = newTerminal };
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

        if (newSubSplit is null || oldActive.Terminal().Parent is not Grid currentParent)
        {
            // Root replacement (oldActive was the entire content of
            // PaneHost), or some unexpected state. Full rebuild handles
            // both cases correctly because there is no nested visual
            // tree to confuse WinUI 3's parent tracking.
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
    }

    /// <summary>
    /// Close the active leaf. If it was the only leaf, raises
    /// <see cref="LastLeafClosed"/>; otherwise the sibling subtree
    /// replaces the parent split and focus moves to the sibling's
    /// first leaf.
    /// </summary>
    public void CloseActive()
    {
        CloseLeaf(_activeLeaf);
    }

    /// <summary>
    /// Tear down every leaf's libghostty surface. Called by
    /// <see cref="MainWindow"/> when the window is closing, since
    /// surface lifetime is decoupled from <c>Unloaded</c> events and
    /// the framework's natural teardown does not free them.
    /// </summary>
    public void DisposeAllLeaves()
    {
        // Every leaf was already disposed one-by-one as the tree
        // collapsed; _root still references the last-closed leaf but
        // walking it here would double-dispose (DisposeSurface is
        // idempotent, but the walk is wasted work and a trap for the
        // next reader).
        if (_allLeavesClosed) return;
        foreach (var leaf in PaneTree.Leaves(_root))
        {
            leaf.Terminal().DisposeSurface();
        }
    }

    /// <summary>
    /// Close a specific leaf. Used both by the keybinding (which closes
    /// <see cref="ActiveLeaf"/>) and by <see cref="TerminalControl.CloseRequested"/>
    /// from libghostty's close-surface callback.
    /// </summary>
    public void CloseLeaf(LeafPane leaf)
    {
        // Detach the terminal from focus tracking BEFORE we drop it.
        leaf.Terminal().GotFocus -= OnTerminalGotFocus;

        // Tear down the libghostty surface for the leaf being removed.
        // The surface lifetime is decoupled from OnLoaded/OnUnloaded
        // (see TerminalControl.DisposeSurface comment), so we have to
        // do it explicitly here.
        leaf.Terminal().DisposeSurface();

        var newRoot = PaneTree.Close(_root, leaf);
        if (newRoot is null)
        {
            // Last leaf - flag the host so DisposeAllLeaves on window
            // close skips iterating a tree whose only node is already
            // disposed, and tell MainWindow to close the window.
            _allLeavesClosed = true;
            LastLeafClosed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _root = newRoot;
        // Focus the first leaf of the (former) sibling subtree. We
        // pick the parent's sibling first so the focus stays close to
        // where the closed pane was.
        var nextActive = PaneTree.FirstLeaf(newRoot);
        _activeLeaf = nextActive;
        Rebuild();
        UpdateHighlightPosition();
        DispatcherQueue.TryEnqueue(() => nextActive.Terminal().Focus(FocusState.Programmatic));
    }

    /// <summary>
    /// Move focus to the leaf nearest the active leaf in the requested
    /// direction. Geometric (uses rendered rects), not tree-order.
    /// No-op if no leaf lies in that direction.
    /// </summary>
    public void FocusDirection(FocusDirection direction)
    {
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

    // Internals ---------------------------------------------------------

    private TerminalControl CreateTerminal()
    {
        var t = _terminalFactory();
        // Host MUST be set before the control is loaded; TerminalControl
        // throws otherwise.
        t.Host = _host;
        t.GotFocus += OnTerminalGotFocus;
        // CloseRequested fires from libghostty's close-surface callback
        // (via GhosttyHost). Route it to the leaf-level close path so
        // multi-pane closing collapses correctly.
        t.CloseRequested += OnTerminalCloseRequested;
        return t;
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
        PositionOverlayOverLeaf(_activeBorderRect, _activeLeaf, insetForStroke: true);

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
    }

    private void PositionOverlayOverLeaf(Rectangle rect, LeafPane leaf, bool insetForStroke)
    {
        var ctl = leaf.Terminal();
        if (ctl.ActualWidth <= 0 || ctl.ActualHeight <= 0)
        {
            rect.Visibility = Visibility.Collapsed;
            return;
        }
        Rect bounds;
        try
        {
            var transform = ctl.TransformToVisual(this);
            bounds = transform.TransformBounds(new Rect(0, 0, ctl.ActualWidth, ctl.ActualHeight));
        }
        catch
        {
            rect.Visibility = Visibility.Collapsed;
            return;
        }
        // For the stroked active border, inset by half the stroke
        // thickness so the 1.5px stroke draws entirely INSIDE the
        // leaf bounds. For dim fills, use the full rect.
        var inset = insetForStroke ? 0.75 : 0.0;
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

    private void Rebuild()
    {
        // Swap the tree visual inside the existing host Grid so the
        // overlay Canvas (the second child) stays on top across
        // rebuilds. Only used for the root-replacement case in
        // Split (and for Close); incremental splits mutate the
        // visual tree directly without a full rebuild.
        if (Content is not Grid hostGrid) return;
        hostGrid.Children.Remove(_treeRoot);
        _treeRoot = BuildVisual(_root);
        hostGrid.Children.Insert(0, _treeRoot);
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
            splitter.Width = 1;
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
            splitter.Height = 1;
            grid.Children.Add(splitter);
        }

        return grid;
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
