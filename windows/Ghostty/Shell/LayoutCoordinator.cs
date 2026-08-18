using System;
using System.Diagnostics;
using Ghostty.Branding;
using Ghostty.Core.Tabs;
using Ghostty.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Ghostty.Shell;

/// <summary>
/// Owns every piece of state that drives the runtime switch
/// between the horizontal and vertical tab layouts: the cross-fade
/// Storyboard, the strip-column width tween, the snap-to-end-state
/// helper, and the concurrent-tween guard.
///
/// Lifted out of MainWindow so the window itself stays a thin
/// composition root. The two tab hosts and the vertical title bar
/// are owned by MainWindow's XAML and passed in via the ctor; this
/// type only animates and toggles them.
///
/// Why the column width tween is code-driven: WinUI 3 has no native
/// GridLengthAnimation. Star/Auto refactoring is a separate piece
/// of work tracked in the deferred review items list.
/// </summary>
internal sealed class LayoutCoordinator
{
    // Width of the icon cell (chevron, tab rows, new-tab control).
    // The new-tab split button stacks its dropdown chevron
    // vertically in this mode (Orientation=Vertical on
    // NewTabSplitButton), so the column can stay narrow. Must stay
    // in sync with the StripColumn Width in VerticalTabHost.xaml.
    // NavigationView LeftCompact default; keep in sync with
    // VerticalTabStrip NavigationView.CompactPaneLength.
    public const double VerticalStripCollapsedWidth = 48;
    public const int SwitchDurationMs = 340;

    // Vertical-mode title bar height. The horizontal host slides this
    // far vertically during the cross-fade so the swap feels like the
    // strip lifting away. Must match VerticalTitleBar.Height in
    // MainWindow.xaml.
    public const double VerticalTitleBarHeight = TabChromeMetrics.TitleRowHeight;

    private static readonly TimeSpan SwitchDuration =
        TimeSpan.FromMilliseconds(SwitchDurationMs);

    // Stagger: outgoing fades first, incoming follows so the morph reads
    // as one continuous motion instead of a flat dissolve.
    private const double IncomingFadeDelay = 0.16;
    private const double OutgoingFadeEnd = 0.78;
    private const double TitleBarSlideDistance = 10;

    // Distance a strip travels between the terminal surface and its lane.
    // Tuned by eye: short enough that the strip never leaves its own lane
    // for long, far enough that the arrival reads as the strip coming out
    // of the terminal rather than settling in place.
    private const double EmergeTravel = 40;

    /// <summary>
    /// Fade the ghost's label when the destination keeps less than this
    /// share of the source width. Above it the text still fits well
    /// enough that fading reads as a flicker.
    /// </summary>
    private const double LabelFadeRatio = 0.6;

    /// <summary>
    /// Rendered frames a primed strip is left visible for. Two is what
    /// it takes for a viewport to reach the repeater and the containers
    /// to come back measurable; the third is slack.
    /// </summary>
    private const int PrimingFrames = 3;


    // Feel constants for the icon spin/pop, tuned by eye. The spin is one
    // full turn; the pop dips the scale partway through and springs back
    // past 1.0 as it lands.
    private const double IconSpinOvershoot = 0.25; // BackEase amplitude on the rotation settle
    private const double IconPopMidpoint = 0.45;   // fraction of the switch where the scale dip bottoms out
    private const double IconPopDipScale = 0.78;   // smallest scale at the dip
    private const double IconPopOvershoot = 0.6;   // BackEase amplitude springing the scale back past 1.0

    private readonly ColumnDefinition _stripColumn;
    private readonly ColumnDefinition _titleBarStripMirror;
    private readonly FrameworkElement _horizontalHost;
    private readonly VerticalTabHost _verticalTabHost;
    private readonly FrameworkElement _verticalHost;
    private readonly Grid _verticalTitleBar;
    // The wintty icon in each layout. Only the incoming one is spun on a
    // switch (see Animate), so the icon looks like the surrounding chrome
    // is shoving it into its new home.
    private readonly FrameworkElement _horizontalIcon;
    private readonly FrameworkElement _verticalIcon;

    // Active-tab morph. The ghost lives on its own canvas above both hosts
    // and is measured against the root so one coordinate space covers the
    // header lane and the sidebar rail.
    private readonly Canvas _morphLayer;
    private readonly FrameworkElement _morphRoot;
    private readonly FrameworkElement _paneHost;

    /// <summary>
    /// Fired when the active-tab ghost lands at the end of an uninterrupted
    /// switch, with the unit direction it was travelling. The window uses
    /// it for a small inertia nudge.
    /// </summary>
    private readonly Action<double, double>? _impact;
    private readonly ITabHost _horizontalTabHost;
    private readonly Func<TabModel?> _activeTab;

    private bool _switching;
    // When true (quake window with a single tab), the strip + vertical
    // title bar are forced hidden regardless of layout mode, leaving only
    // the pane host. Snap and Animate both honor it so the layout toggle
    // cannot resurrect the strip.
    private bool _stripHidden;
    // Quake window: the top VerticalTitleBar (layout-toggle chevron +
    // window title) is suppressed entirely. The wintty icon at the top of
    // the vertical strip becomes the topmost element above the tabs. Layout
    // switching still works via the keyboard chord.
    private bool _verticalTitleBarSuppressed;
    // Per-frame handler rather than a DispatcherTimer: the strip column is
    // resized from managed code every frame, and a queued timer competing
    // with the switch storyboard and its layout passes got starved down to
    // two ticks in 190ms -- the column lurched instead of sliding.
    // CompositionTarget.Rendering is tied to composition, so it keeps step.
    private EventHandler<object>? _columnFrame;

    public LayoutCoordinator(
        ColumnDefinition stripColumn,
        ColumnDefinition titleBarStripMirror,
        FrameworkElement horizontalHost,
        VerticalTabHost verticalTabHost,
        Grid verticalTitleBar,
        FrameworkElement horizontalIcon,
        ITabHost horizontalTabHost,
        Canvas morphLayer,
        FrameworkElement morphRoot,
        FrameworkElement paneHost,
        Func<TabModel?> activeTab,
        Action<double, double>? impact = null)
    {
        _impact = impact;
        _horizontalTabHost = horizontalTabHost;
        _morphLayer = morphLayer;
        _morphRoot = morphRoot;
        _paneHost = paneHost;
        _activeTab = activeTab;
        _stripColumn = stripColumn;
        _titleBarStripMirror = titleBarStripMirror;
        _horizontalHost = horizontalHost;
        _verticalTabHost = verticalTabHost;
        _verticalHost = verticalTabHost;
        _verticalTitleBar = verticalTitleBar;
        _horizontalIcon = horizontalIcon;
        _verticalIcon = verticalTabHost.IconBadge;

        // Pin toggle snaps immediately -- NavView needs the full column width
        // before IsPaneOpen sticks; a tween left MUXC auto-closing the pane.
        _verticalTabHost.StripWidthChangeRequested += (_, width) =>
            SnapStripColumn(width);
    }

    public bool IsSwitching => _switching;

    /// <summary>
    /// Snap both hosts and the vertical title bar to the end state
    /// for <paramref name="verticalTabs"/>. Used at construction
    /// (no animation needed) and from the Storyboard Completed
    /// handler to guarantee a consistent end state regardless of
    /// mid-flight cancellation.
    /// </summary>
    public void Snap(bool verticalTabs)
    {
        var w = verticalTabs ? _verticalTabHost.CurrentStripTarget : 0;
        _stripColumn.Width = new GridLength(w);
        _titleBarStripMirror.Width = new GridLength(w);
        _verticalTabHost.SetInternalStripWidth(
            verticalTabs ? _verticalTabHost.CurrentStripTarget : VerticalStripCollapsedWidth);

        _verticalHost.Opacity = verticalTabs ? 1 : 0;
        _verticalHost.Visibility = verticalTabs ? Visibility.Visible : Visibility.Collapsed;
        _verticalHost.IsHitTestVisible = verticalTabs;

        _horizontalHost.Opacity = verticalTabs ? 0 : 1;
        _horizontalHost.Visibility = verticalTabs ? Visibility.Collapsed : Visibility.Visible;
        _horizontalHost.IsHitTestVisible = !verticalTabs;

        if (_verticalTitleBarSuppressed)
        {
            _verticalTitleBar.Visibility = Visibility.Collapsed;
            _verticalTitleBar.Opacity = 0;
        }
        else
        {
            _verticalTitleBar.Visibility = verticalTabs ? Visibility.Visible : Visibility.Collapsed;
            _verticalTitleBar.Opacity = verticalTabs ? 1 : 0;
        }

        // Reset any dangling transform offsets so future switches start
        // from origin. Snap is the single end-state authority, so it
        // also returns the spun icons to identity -- without this, a
        // switch interrupted by SetStripHidden / SuppressVerticalTitleBar
        // (which call Snap directly) could leave an icon rotated or
        // scaled once the strip is shown again.
        GetOrCreateTranslate(_verticalHost).X = 0;
        GetOrCreateTranslate(_verticalHost).Y = 0;
        GetOrCreateTranslate(_horizontalHost).X = 0;
        GetOrCreateTranslate(_horizontalHost).Y = 0;
        ResetIconTransform(_horizontalIcon);
        ResetIconTransform(_verticalIcon);

        if (!_verticalTitleBarSuppressed)
        {
            GetOrCreateTranslate(_verticalTitleBar).X = 0;
            GetOrCreateTranslate(_verticalTitleBar).Y = 0;
        }

        if (verticalTabs)
            _verticalTabHost.ConfigureTitleBarIconMode(_verticalTitleBarSuppressed);

        if (verticalTabs)
            _verticalTabHost.SyncSelectionFromManager();

        if (_stripHidden)
        {
            // Collapse everything in the strip/title row + column,
            // leaving only the pane host (row 1, col 1) visible.
            _stripColumn.Width = new GridLength(0);
            _titleBarStripMirror.Width = new GridLength(0);
            _horizontalHost.Visibility = Visibility.Collapsed;
            _horizontalHost.IsHitTestVisible = false;
            _verticalHost.Visibility = Visibility.Collapsed;
            _verticalHost.IsHitTestVisible = false;
            _verticalTitleBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Force the tab strip (and vertical title bar) hidden or restore it
    /// to the normal end-state for the current layout. Used by the quake
    /// window to hide tab chrome when only one tab is open.
    /// </summary>
    public void SetStripHidden(bool hidden, bool verticalTabs)
    {
        _stripHidden = hidden;
        Snap(verticalTabs);
    }

    public void SuppressVerticalTitleBar(bool suppress, bool verticalTabs)
    {
        _verticalTitleBarSuppressed = suppress;
        Snap(verticalTabs);
    }

    /// <summary>
    /// Cross-fade + slide animation between horizontal and vertical
    /// layouts. Chrome transforms run in a compositor <see cref="Storyboard"/>;
    /// strip column width tweens in parallel because WinUI 3 has no native
    /// GridLengthAnimation.
    /// </summary>
    public void Animate(bool verticalTabs, Action? onCompleted = null)
    {
        if (_stripHidden)
        {
            Snap(verticalTabs);
            onCompleted?.Invoke();
            return;
        }
        if (_switching) return;
        _switching = true;

        var targetColWidth = verticalTabs ? _verticalTabHost.CurrentStripTarget : 0;
        var fromColWidth = _stripColumn.Width.Value;

        if (verticalTabs)
            _verticalTabHost.ConfigureTitleBarIconMode(_verticalTitleBarSuppressed);

        if (!_verticalTitleBarSuppressed)
            _verticalTitleBar.Visibility = Visibility.Visible;
        _verticalHost.Visibility = Visibility.Visible;
        _horizontalHost.Visibility = Visibility.Visible;

        var incoming = verticalTabs ? _verticalHost : _horizontalHost;
        var outgoing = verticalTabs ? _horizontalHost : _verticalHost;
        // Both strips travel along the axis that connects them to the
        // terminal surface: the incoming one emerges out of the terminal and
        // settles into its lane, the outgoing one retreats back into it.
        // Sliding in from beyond the window edge instead reads as chrome
        // appearing from nowhere.
        //
        // Going vertical, the sidebar's lane is the left edge and the
        // terminal is to its right, so the incoming strip starts right of
        // home and moves left. Going horizontal, the header's lane is the
        // top and the terminal is below it, so the incoming strip starts
        // below home and rises.
        var incomingOffset = verticalTabs
            ? new Windows.Foundation.Point(EmergeTravel, 0)
            : new Windows.Foundation.Point(0, EmergeTravel);
        var outgoingOffset = verticalTabs
            ? new Windows.Foundation.Point(0, EmergeTravel)
            : new Windows.Foundation.Point(EmergeTravel, 0);

        StopColumnTween();

        // The strip column is moved once per switch, never tweened. Changing
        // it resizes the terminal surface, which re-renders synchronously on
        // the UI thread, so any per-frame tween starves the thread it runs
        // on: measured over a switch, a DispatcherTimer and
        // CompositionTarget.Rendering each got two frames in before the
        // column jumped the rest of the way -- the lane lurched open under
        // the arriving strip. Opening happens up front, so the strip and the
        // active-tab ghost travel toward a lane that is already the right
        // size (the header spans both grid columns, so nothing measured
        // below shifts); closing leaves the lane in place for the strip to
        // retreat through and lets Snap collapse it once the switch lands.
        //
        // This is also the switch's only pane-mode change. The measure pass
        // used to stage the target width and put it back, which toggled the
        // rail expanded -> compact -> expanded inside one blocked UI turn,
        // and MUXC's starved pane transition left the rows stuck compact in
        // a full-width lane.
        if (targetColWidth > fromColWidth)
        {
            SnapStripColumn(targetColWidth);
            _morphRoot.UpdateLayout();
        }
        else if (fromColWidth > 0)
        {
            StartPaneReveal(fromColWidth);
        }

        // Staged before any transform is applied below. TransformToVisual
        // reads whatever offset the strip is already carrying, so measuring
        // after the incoming translate would aim the ghost at the strip's
        // pre-animation position and leave it EmergeTravel short of home.
        PrepareActiveTabMorph(verticalTabs, incomingOffset);

        incoming.IsHitTestVisible = true;
        outgoing.IsHitTestVisible = false;
        var incomingTx = GetOrCreateTranslate(incoming);
        incomingTx.X = incomingOffset.X;
        incomingTx.Y = incomingOffset.Y;
        incoming.Opacity = 0;

        var sb = new Storyboard();
        sb.Children.Add(MakeStaggeredFadeIn(incoming));
        sb.Children.Add(MakeStaggeredFadeOut(outgoing, outgoing.Opacity));

        if (!_verticalTitleBarSuppressed)
            AddTitleBarAnimations(sb, verticalTabs);

        sb.Children.Add(MakeIncomingSlideAnim(incoming, "X", incomingTx.X, 0));
        sb.Children.Add(MakeIncomingSlideAnim(incoming, "Y", incomingTx.Y, 0));
        var outgoingTx = GetOrCreateTranslate(outgoing);
        sb.Children.Add(MakeOutgoingSlideAnim(outgoing, "X", outgoingTx.X, outgoingOffset.X));
        sb.Children.Add(MakeOutgoingSlideAnim(outgoing, "Y", outgoingTx.Y, outgoingOffset.Y));

        var spin = verticalTabs ? -360.0 : 360.0;
        var incomingIcon = verticalTabs ? _verticalIcon : _horizontalIcon;
        var outgoingIcon = verticalTabs ? _horizontalIcon : _verticalIcon;
        if (!PrepareIconGhost(sb, outgoingIcon, incomingIcon, spin))
        {
            SpinIconInPlace(sb, incomingIcon, spin,
                -incomingOffset.X, -incomingOffset.Y, 0, 0, incoming: true);
            SpinIconInPlace(sb, outgoingIcon, spin,
                0, 0, -outgoingOffset.X, -outgoingOffset.Y, incoming: false);
        }

        sb.Completed += (_, _) =>
        {
            // Only a ghost that actually flew has anything to slam into,
            // and only a switch that ran to completion has a landing --
            // the fallback paths below fast-forward without motion.
            var landed = _morph is not null;
            FinishSwitch(verticalTabs, onCompleted);
            if (landed)
                _impact?.Invoke(verticalTabs ? -1 : 0, verticalTabs ? 0 : -1);
        };

        // If Begin throws, Completed never fires and _switching stays
        // latched -- every later layout toggle (keybind, palette, settings)
        // would silently no-op for the life of the window. Land the switch
        // without the animation instead.
        try
        {
            sb.Begin();
        }
        catch (Exception)
        {
            FinishSwitch(verticalTabs, onCompleted);
            return;
        }

        // Kept separate: a morph that will not start should cost the ghost,
        // not the whole switch. Dropping it here leaves the plain cross-fade.
        try
        {
            _morphStoryboard?.Begin();
        }
        catch (Exception)
        {
            FinishActiveTabMorph();
        }
    }


    /// <summary>
    /// One active-tab morph in flight: the ghost plus the two real elements
    /// it stands in for, so Finish can put them all back. To and Waiting are
    /// mutable because the destination element may not exist yet when the
    /// morph starts; see <see cref="PrepareActiveTabMorph"/>.
    /// </summary>
    private sealed class ActiveTabMorph
    {
        internal required TabMorphGhost Ghost { get; init; }
        internal required FrameworkElement? From { get; init; }
        internal FrameworkElement? To { get; set; }
        internal EventHandler<object>? Waiting { get; set; }
    }

    private ActiveTabMorph? _morph;
    private Storyboard? _morphStoryboard;

    /// <summary>
    /// How long to keep waiting for the incoming host to realize the active
    /// tab's container. Long enough for a couple of frames, short enough
    /// that a ghost which will never find a home is dropped while the
    /// cross-fade underneath still has time to cover for it.
    /// </summary>
    private static readonly TimeSpan RealizationGrace = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Give a collapsed horizontal strip the rendered frames it needs to
    /// realize its item containers.
    ///
    /// TabView's ItemsRepeater realizes on an effective viewport, and a
    /// viewport only arrives once the element has actually rendered --
    /// UpdateLayout will not conjure one. A header that has been collapsed
    /// since launch therefore has no container for any tab, so nothing for
    /// the morph to aim at on the first switch to it. Showing it fully
    /// transparent for a few frames costs nothing on screen and leaves the
    /// containers in place, where they survive being collapsed again.
    ///
    /// Deliberately never primes the vertical host. Its NavigationView
    /// realizes rows synchronously under UpdateLayout, so the first switch
    /// toward it measures fine unprimed -- and showing the never-laid-out
    /// NavigationView from the window constructor crashed XAML's measure
    /// walk (InvalidCastException in MeasureOverride) seconds into every
    /// horizontal-mode launch.
    /// </summary>
    public void PrimeHiddenStrip()
    {
        if (_switching || _stripHidden) return;
        var host = _horizontalHost.Visibility == Visibility.Collapsed
            ? _horizontalHost
            : null;
        if (host is null) return;

        host.Opacity = 0;
        host.IsHitTestVisible = false;
        host.Visibility = Visibility.Visible;

        var frames = 0;
        EventHandler<object>? onFrame = null;
        onFrame = (_, _) =>
        {
            if (++frames < PrimingFrames) return;
            CompositionTarget.Rendering -= onFrame;
            // A switch started meanwhile owns the host's visibility now.
            if (_switching) return;
            host.Visibility = Visibility.Collapsed;
        };
        CompositionTarget.Rendering += onFrame;
    }

    private static Rect RectIn(FrameworkElement root, FrameworkElement el)
    {
        if (el.ActualWidth <= 0 || el.ActualHeight <= 0) return default;
        try
        {
            var origin = el.TransformToVisual(root).TransformPoint(new Point(0, 0));
            return new Rect(origin.X, origin.Y, el.ActualWidth, el.ActualHeight);
        }
        catch
        {
            // TransformToVisual throws when the element is not in the same
            // tree as root (collapsed ancestor, mid-teardown). No rect means
            // no morph, which degrades to the plain cross-fade.
            return default;
        }
    }

    /// <summary>
    /// Put a ghost of the active tab on the outgoing rect and drive it to
    /// the incoming one, hiding both real elements meanwhile. Assigns
    /// <see cref="_morph"/>, and leaves it null when there is nothing to
    /// morph, in which case the switch is just the cross-fade.
    /// </summary>
    private void PrepareActiveTabMorph(
        bool verticalTabs, Point incomingOffset)
    {
        // A ghost left over from an aborted switch would never be collected
        // otherwise: the field is about to be overwritten.
        FinishActiveTabMorph();

        if (_activeTab() is not { } tab) return;

        var outgoing = verticalTabs ? _horizontalTabHost : (ITabHost)_verticalTabHost;
        var incoming = verticalTabs ? (ITabHost)_verticalTabHost : _horizontalTabHost;

        var from = outgoing.TabElement(tab);
        if (from is null) return;
        var fromRect = RectIn(_morphRoot, from);
        if (fromRect.Width <= 0) return;

        var (to, toRect) = MeasureIncomingTab(incoming, tab);

        var chrome = _verticalTabHost.ActiveRowChrome(tab);
        // Rail rows are square and meet the pane edge-to-edge; header tabs
        // keep the tab-like rounding on their top corners only.
        var destinationShape = verticalTabs
            ? new CornerRadius(0)
            : new CornerRadius(4, 4, 0, 0);
        var ghost = new TabMorphGhost(tab, chrome.Fill, chrome.Foreground, destinationShape)
        {
            Width = fromRect.Width,
            Height = fromRect.Height,
        };
        ghost.Translate.X = fromRect.X;
        ghost.Translate.Y = fromRect.Y;
        _morphLayer.Children.Add(ghost);

        // Hide both real ones for the duration. Leaving them visible is
        // exactly the double image the ghost exists to remove. The rail's
        // selected-row fill is a separate overlay, so it has to be told
        // as well.
        from.Opacity = 0;
        if (to is not null) to.Opacity = 0;
        _verticalTabHost.SetSelectionRowSuppressed(true);

        var morph = new ActiveTabMorph { Ghost = ghost, From = from, To = to };
        _morph = morph;

        if (toRect.Width > 0)
        {
            StageMorphAnimations(morph, fromRect, toRect, SwitchDuration);
            return;
        }

        // No container to aim at yet. A host that has been collapsed since
        // launch has never realized its items: ItemsRepeater realizes on an
        // effective viewport, which arrives with a rendered frame, so no
        // amount of UpdateLayout conjures one. The host is visible from here
        // on, so the container lands within a frame or two -- hold the ghost
        // on the outgoing rect and stage the moment it does.
        var clock = Stopwatch.StartNew();
        EventHandler<object>? waiting = null;
        waiting = (_, _) =>
        {
            if (!ReferenceEquals(_morph, morph))
            {
                _morphRoot.LayoutUpdated -= waiting;
                return;
            }

            var late = incoming.TabElement(tab);
            var rect = late is null ? default : RectIn(_morphRoot, late);

            // Past the grace period a ghost would have sat still for a
            // noticeable slice of the switch and then lurched. Drop it and
            // let the cross-fade carry the rest.
            if (rect.Width <= 0 && clock.Elapsed <= RealizationGrace) return;

            _morphRoot.LayoutUpdated -= waiting;
            morph.Waiting = null;
            if (rect.Width <= 0 || clock.Elapsed > RealizationGrace)
            {
                FinishActiveTabMorph();
                return;
            }

            // The strip is already carrying its travel offset by now, so the
            // rect measured through it is short of where the tab comes to
            // rest.
            rect.X -= incomingOffset.X;
            rect.Y -= incomingOffset.Y;

            morph.To = late;
            late!.Opacity = 0;

            // Spend only what is left of the switch, so the ghost still
            // lands with the cross-fade rather than after it.
            StageMorphAnimations(
                morph, fromRect, rect, SwitchDuration - clock.Elapsed);
            try
            {
                _morphStoryboard?.Begin();
            }
            catch (Exception)
            {
                FinishActiveTabMorph();
            }
        };
        morph.Waiting = waiting;
        _morphRoot.LayoutUpdated += waiting;
    }

    /// <summary>
    /// Where the active tab will sit once the switch lands. By the time
    /// this runs the lane is already at its final width -- Animate opens it
    /// before staging the morph, and a closing switch leaves it open -- so
    /// the incoming host only needs a layout pass to realize its rects
    /// after being made visible. No width is staged here: setting and
    /// restoring the strip width toggled the rail's pane mode twice inside
    /// one blocked UI turn, which left MUXC's rows stuck at compact size.
    /// </summary>
    private (FrameworkElement? Element, Rect Rect) MeasureIncomingTab(
        ITabHost incoming, TabModel tab)
    {
        _morphRoot.UpdateLayout();

        var to = incoming.TabElement(tab);
        var toRect = to is null ? default : RectIn(_morphRoot, to);
        return (to, toRect);
    }

    /// <summary>
    /// Drive the ghost from one rect to the other. Width and Height are
    /// dependent animations because the label must re-lay-out rather than
    /// scale: squashing a 240px header tab into a 48px rail smears the
    /// text, which is the artifact in a different costume. One small
    /// element for the length of a switch is a cheap layout pass.
    /// </summary>
    /// <summary>
    /// Share of the flight the ghost spends changing size. Position runs
    /// the full duration; size and label settle at this fraction, so the
    /// ghost covers the last stretch already in its destination shape and
    /// the landing is a pure glide instead of arrive-then-resize.
    /// </summary>
    private const double ShapeSettleFraction = 0.75;

    private void StageMorphAnimations(
        ActiveTabMorph morph, Rect from, Rect to, TimeSpan duration)
    {
        var settle = duration * ShapeSettleFraction;
        var sb = new Storyboard();
        Add(sb, morph.Ghost.Translate, "X", from.X, to.X);
        Add(sb, morph.Ghost.Translate, "Y", from.Y, to.Y);
        Add(sb, morph.Ghost, "Width", from.Width, to.Width, dependent: true, settle);
        Add(sb, morph.Ghost, "Height", from.Height, to.Height, dependent: true, settle);

        // The label survives a modest width change but not the collapse to
        // a 48px rail, so it fades on the leg that loses most of its room
        // and fades back in on the return leg.
        var shrinking = to.Width < from.Width * LabelFadeRatio;
        var growing = from.Width < to.Width * LabelFadeRatio;
        if (shrinking || growing)
        {
            morph.Ghost.Label.Opacity = shrinking ? 1 : 0;
            Add(sb, morph.Ghost.Label, "Opacity",
                shrinking ? 1 : 0, shrinking ? 0 : 1, dependent: false, settle);
        }

        _morphStoryboard = sb;

        void Add(
            Storyboard sb, DependencyObject target, string path,
            double f, double t, bool dependent = false, TimeSpan? span = null)
        {
            var anim = new DoubleAnimation
            {
                From = f,
                To = t,
                Duration = new Duration(span ?? duration),
                EnableDependentAnimation = dependent,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, path);
            sb.Children.Add(anim);
        }
    }

    /// <summary>
    /// Drop the ghost and give the real elements their opacity back.
    /// Safe to call twice; FinishSwitch and the failure paths all do.
    /// </summary>
    private void FinishActiveTabMorph()
    {
        _morphStoryboard?.Stop();
        _morphStoryboard = null;
        if (_morph is null) return;
        if (_morph.Waiting is not null)
        {
            _morphRoot.LayoutUpdated -= _morph.Waiting;
            _morph.Waiting = null;
        }
        _morphLayer.Children.Remove(_morph.Ghost);
        if (_morph.From is not null) _morph.From.Opacity = 1;
        if (_morph.To is not null) _morph.To.Opacity = 1;
        _verticalTabHost.SetSelectionRowSuppressed(false);
        _morph = null;
    }

    private void FinishSwitch(bool verticalTabs, Action? onCompleted)
    {
        if (!_switching) return;
        StopColumnTween();
        FinishActiveTabMorph();
        FinishIconGhost();
        FinishPaneReveal();
        Snap(verticalTabs);
        _switching = false;
        onCompleted?.Invoke();
    }

    /// <summary>
    /// Snap the vertical strip column to <paramref name="width"/> with no
    /// animation. Used by the pane-toggle button so NavigationView and the
    /// outer shell stay in lockstep.
    /// </summary>
    public void SnapStripColumn(double width)
    {
        StopColumnTween();
        _stripColumn.Width = new GridLength(width);
        _titleBarStripMirror.Width = new GridLength(width);
        _verticalTabHost.SetInternalStripWidth(width);
    }

    /// <summary>
    /// Tween <see cref="ColumnDefinition.Width"/> from its current
    /// value to <paramref name="to"/>. Used by the chevron expand
    /// path inside <see cref="VerticalTabHost"/>; the runtime layout
    /// switch above bundles its column tween into the cross-fade
    /// Storyboard instead.
    ///
    /// Cancels any in-flight column tween so the chevron toggle and
    /// the layout switch cannot race on the same column.
    /// </summary>
    public void TweenStripColumn(double from, double to, Action<double>? onTick = null)
    {
        StopColumnTween();
        var sw = Stopwatch.StartNew();
        var span = TimeSpan.FromMilliseconds(SwitchDurationMs);
        EventHandler<object>? frame = null;
        frame = (_, _) =>
        {
            var t = Math.Min(sw.Elapsed / span, 1.0);
            var value = from + (to - from) * EaseInOutCubic(t);
            _stripColumn.Width = new GridLength(value);
            _titleBarStripMirror.Width = new GridLength(value);
            onTick?.Invoke(value);
            if (t >= 1.0 && ReferenceEquals(_columnFrame, frame))
                StopColumnTween();
        };
        _columnFrame = frame;
        CompositionTarget.Rendering += frame;
    }

    private void StopColumnTween()
    {
        if (_columnFrame is null) return;
        CompositionTarget.Rendering -= _columnFrame;
        _columnFrame = null;
    }

    private static TranslateTransform GetOrCreateTranslate(FrameworkElement fe)
    {
        if (fe.RenderTransform is TranslateTransform t) return t;
        var nt = new TranslateTransform();
        fe.RenderTransform = nt;
        return nt;
    }

    // Spin + pop one icon while holding it at a fixed anchor. The
    // translate runs from/to the negative of the host's slide so it
    // exactly cancels it (same easing + duration as the host slide),
    // leaving only the in-place rotate and scale visible.
    /// <summary>
    /// Hold one app icon on screen for the whole switch.
    ///
    /// Each host owns its own badge, so cross-fading the hosts cross-fades
    /// the icon with them: through the middle of the switch neither copy is
    /// fully opaque and the mark visibly dips out. Both hosts place the
    /// badge on the same pixel, so a single stand-in can hold that spot at
    /// full opacity while the real ones are hidden -- and being outside the
    /// sliding hosts it needs no counter-slide to stay put.
    ///
    /// Returns false when the badge cannot be measured, in which case the
    /// caller falls back to spinning the hosts' own icons.
    /// </summary>
    private bool PrepareIconGhost(
        Storyboard sb, FrameworkElement outgoing, FrameworkElement incoming, double spin)
    {
        FinishIconGhost();

        var rect = RectIn(_morphRoot, outgoing);
        if (rect.Width <= 0) return false;

        var ghost = new Image
        {
            Source = new BitmapImage(AppIconSource.Current),
            Width = AppIconSize,
            Height = AppIconSize,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        var scale = new ScaleTransform();
        var rotate = new RotateTransform();
        var translate = new TranslateTransform
        {
            X = rect.X + ((rect.Width - AppIconSize) / 2),
            Y = rect.Y + ((rect.Height - AppIconSize) / 2),
        };
        ghost.RenderTransform = new TransformGroup
        {
            Children = { scale, rotate, translate },
        };

        _morphLayer.Children.Add(ghost);
        _iconGhost = ghost;
        outgoing.Opacity = 0;
        incoming.Opacity = 0;
        _iconGhostHidden = (outgoing, incoming);

        sb.Children.Add(MakeRotateAnim(rotate, 0, spin));
        sb.Children.Add(MakePopAnim(scale, "ScaleX"));
        sb.Children.Add(MakePopAnim(scale, "ScaleY"));
        return true;
    }

    private void FinishIconGhost()
    {
        if (_iconGhost is not null)
        {
            _morphLayer.Children.Remove(_iconGhost);
            _iconGhost = null;
        }
        if (_iconGhostHidden is not { } pair) return;
        pair.Outgoing.Opacity = 1;
        pair.Incoming.Opacity = 1;
        _iconGhostHidden = null;
    }

    /// <summary>
    /// Let the terminal grow fluidly into the lane a closing switch vacates.
    ///
    /// The lane itself cannot be tweened: every strip-column change resizes
    /// the terminal surface, which re-renders synchronously on the UI
    /// thread, so a per-frame tween starves the thread that drives it. The
    /// terminal is instead resized ONCE, up front -- a negative left margin
    /// extends it under the still-open lane -- and hidden there by an
    /// InsetClip whose left inset sweeps to zero. The sweep runs on the
    /// compositor's thread, so the terminal's edge glides left at full
    /// frame rate no matter how busy the UI thread is, advancing over the
    /// fading strip (both tab hosts sit at ZIndex -1, below the pane
    /// container).
    ///
    /// FinishSwitch then collapses the column and removes the margin in the
    /// same layout pass, which cancel out: the terminal's arrange rect is
    /// identical before and after, so landing costs no second resize.
    /// </summary>
    private void StartPaneReveal(double laneWidth)
    {
        FinishPaneReveal();
        try
        {
            _savedPaneMargin = _paneHost.Margin;
            _paneRevealActive = true;
            _paneHost.Margin = new Thickness(
                _savedPaneMargin.Left - laneWidth,
                _savedPaneMargin.Top,
                _savedPaneMargin.Right,
                _savedPaneMargin.Bottom);
            _morphRoot.UpdateLayout();

            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                .GetElementVisual(_paneHost);
            var compositor = visual.Compositor;
            var clip = compositor.CreateInsetClip((float)laneWidth, 0f, 0f, 0f);
            visual.Clip = clip;

            var sweep = compositor.CreateScalarKeyFrameAnimation();
            sweep.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.65f, 0f),
                new System.Numerics.Vector2(0.35f, 1f)));
            sweep.Duration = SwitchDuration;
            clip.StartAnimation(nameof(Microsoft.UI.Composition.InsetClip.LeftInset), sweep);
        }
        catch (Exception)
        {
            // Composition refused; put the margin back and fall back to the
            // plain cross-fade with the lane collapsing at the end.
            FinishPaneReveal();
        }
    }

    /// <summary>
    /// Safe to call twice; FinishSwitch and the failure path both do.
    /// </summary>
    private void FinishPaneReveal()
    {
        if (!_paneRevealActive) return;
        _paneRevealActive = false;
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
            .GetElementVisual(_paneHost).Clip = null;
        _paneHost.Margin = _savedPaneMargin;
    }

    private bool _paneRevealActive;
    private Thickness _savedPaneMargin;

    private Image? _iconGhost;
    private (FrameworkElement Outgoing, FrameworkElement Incoming)? _iconGhostHidden;

    /// <summary>Matches the ImageIcon inside AppIconBadge.</summary>
    private const double AppIconSize = 16;

    private static void SpinIconInPlace(
        Storyboard sb, FrameworkElement? icon, double spin,
        double fromX, double fromY, double toX, double toY,
        bool incoming)
    {
        if (icon is null) return;
        var (scale, rotate, translate) = EnsureIconTransform(icon);
        rotate.Angle = 0;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = fromX;
        translate.Y = fromY;
        sb.Children.Add(MakeRotateAnim(rotate, 0, spin));
        sb.Children.Add(MakePopAnim(scale, "ScaleX"));
        sb.Children.Add(MakePopAnim(scale, "ScaleY"));
        sb.Children.Add(MakeIconCounterSlideAnim(translate, "X", fromX, toX, incoming));
        sb.Children.Add(MakeIconCounterSlideAnim(translate, "Y", fromY, toY, incoming));
    }

    // Give the icon a Scale+Rotate+Translate group: scale and rotate
    // pivot on its centre (RenderTransformOrigin), the translate (applied
    // last, so it stays axis-aligned) cancels the host slide.
    private static (ScaleTransform Scale, RotateTransform Rotate, TranslateTransform Translate) EnsureIconTransform(FrameworkElement fe)
    {
        if (fe.RenderTransform is TransformGroup g
            && g.Children.Count == 3
            && g.Children[0] is ScaleTransform existingScale
            && g.Children[1] is RotateTransform existingRotate
            && g.Children[2] is TranslateTransform existingTranslate)
            return (existingScale, existingRotate, existingTranslate);

        var scale = new ScaleTransform();
        var rotate = new RotateTransform();
        var translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rotate);
        group.Children.Add(translate);
        fe.RenderTransform = group;
        fe.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return (scale, rotate, translate);
    }

    private static void ResetIconTransform(FrameworkElement? fe)
    {
        if (fe is null) return;
        var (scale, rotate, translate) = EnsureIconTransform(fe);
        rotate.Angle = 0;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;
    }

    private void AddTitleBarAnimations(Storyboard sb, bool verticalTabs)
    {
        var titleTx = GetOrCreateTranslate(_verticalTitleBar);
        if (verticalTabs)
        {
            _verticalTitleBar.Opacity = 0;
            titleTx.Y = -TitleBarSlideDistance;
            sb.Children.Add(MakeStaggeredFadeIn(_verticalTitleBar));
            sb.Children.Add(MakeIncomingSlideAnim(_verticalTitleBar, "Y", titleTx.Y, 0));
        }
        else
        {
            sb.Children.Add(MakeStaggeredFadeOut(_verticalTitleBar, _verticalTitleBar.Opacity));
            sb.Children.Add(MakeOutgoingSlideAnim(
                _verticalTitleBar, "Y", titleTx.Y, -TitleBarSlideDistance));
        }
    }

    private static double EaseInOutCubic(double t)
        => t < 0.5
            ? 4.0 * t * t * t
            : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0;

    private static DoubleAnimationUsingKeyFrames MakeStaggeredFadeIn(FrameworkElement target)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0,
        });
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(
                TimeSpan.FromMilliseconds(SwitchDurationMs * IncomingFadeDelay)),
            Value = 0,
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(SwitchDuration),
            Value = 1,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        return anim;
    }

    private static DoubleAnimationUsingKeyFrames MakeStaggeredFadeOut(
        FrameworkElement target, double fromOpacity)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = fromOpacity,
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(
                TimeSpan.FromMilliseconds(SwitchDurationMs * OutgoingFadeEnd)),
            Value = 0,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        });
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(SwitchDuration),
            Value = 0,
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        return anim;
    }

    private static DoubleAnimation MakeRotateAnim(RotateTransform target, double from, double to)
    {
        // A touch of back-ease overshoot at the end sells the "shoved
        // and settling" feel rather than a mechanical stop.
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(SwitchDuration),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = IconSpinOvershoot },
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Angle");
        return anim;
    }

    // Dip the icon's scale mid-spin then spring back past 1.0, so it
    // pops as it lands.
    private static DoubleAnimationUsingKeyFrames MakePopAnim(ScaleTransform target, string axis)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 1.0,
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(SwitchDurationMs * IconPopMidpoint)),
            Value = IconPopDipScale,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(SwitchDuration),
            Value = 1.0,
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = IconPopOvershoot },
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, axis);
        return anim;
    }

    private static DoubleAnimation MakeIconCounterSlideAnim(
        TranslateTransform target, string axis, double from, double to, bool incoming)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(SwitchDuration),
            EasingFunction = incoming
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 }
                : new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, axis);
        return anim;
    }

    private static DoubleAnimation MakeIncomingSlideAnim(
        FrameworkElement target, string axis, double from, double to)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(SwitchDuration),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 },
        };
        Storyboard.SetTarget(anim, target.RenderTransform);
        Storyboard.SetTargetProperty(anim, axis);
        return anim;
    }

    private static DoubleAnimation MakeOutgoingSlideAnim(
        FrameworkElement target, string axis, double from, double to)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(SwitchDuration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(anim, target.RenderTransform);
        Storyboard.SetTargetProperty(anim, axis);
        return anim;
    }
}
