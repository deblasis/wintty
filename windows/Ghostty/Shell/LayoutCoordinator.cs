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

    // The cross-fade's hand-over. Both numbers, and the easing each end
    // carries, exist to give the switch a LEADER: at no point should two
    // tab strips both be most of the way present, which is what the eye
    // reads as "something is wrong" rather than "this is changing".
    //
    // They used to say the opposite. The outgoing end eased IN -- slow to
    // leave, so it sat near full opacity through the first half -- while
    // the incoming end eased OUT, rushing to near full early. Sampled
    // mid-flight the two hosts measured 0.89/0.58 and 0.65/0.86: a
    // complete sidebar and a complete header on screen together, in
    // different lanes, with the pane reveal already slicing the departing
    // one into unreadable fragments.
    //
    // Flipped, the departing strip is under a tenth by a third of the way
    // in and the arriving one does not pass half until well past that.
    //
    // The delay was 0.22 and is now 0.12, because the first version bought
    // the leader at too high a price going TO vertical. Filmed at 30fps
    // and read against the state track: a third of the way in the outgoing
    // header was down to 0.22 and the incoming rail was still at 0.075, so
    // the sidebar lane was a dark empty column while the active tab was
    // visibly flying towards it. The tab had nowhere to be going. At 0.12
    // the same instant reads about 0.14 against 0.47 -- the departing strip
    // is still plainly the one leaving, and the lane it is leaving for
    // exists.
    //
    // The margin that keeps the leader is worth stating because it is what
    // these two numbers are for. Outgoing passes below half at 0.12 of the
    // flight; incoming passes above half at 0.30. Nothing may close that
    // gap: the layout-switch filmstrip asserts on it directly.
    private const double IncomingFadeDelay = 0.12;
    private const double OutgoingFadeEnd = 0.60;
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
    /// Staged when the active-tab ghost's flight is staged, with the unit
    /// direction it travels and how long until it lands. The window uses
    /// it for a small inertia nudge.
    ///
    /// The DELAY is the point. This used to fire from the switch
    /// Storyboard's Completed handler, which is raised on the UI thread and
    /// therefore queues behind whatever the terminal's own resize is doing:
    /// measured, the nudge started at about 560ms on a switch whose visual
    /// motion ended at 340ms. A punctuation mark that lands after the
    /// sentence ends is not punctuation. Handing the delay over instead
    /// lets the window schedule the nudge on the compositor, which runs it
    /// at the landing whatever the UI thread is doing.
    /// </summary>
    private readonly Action<double, double, TimeSpan>? _impact;
    private readonly ITabHost _horizontalTabHost;
    private readonly Func<TabModel?> _activeTab;

    /// <summary>
    /// Whether this switch may move. Asked per switch, never cached: the
    /// preference can change under a running window, and reading it can
    /// throw in packaged contexts, so the window's own reader owns the
    /// fail-open.
    /// </summary>
    private readonly Func<bool>? _motionEnabled;

    private bool _switching;
    // The Storyboard staged by the most recent switch, non-null exactly while
    // that switch is in flight: Animate stages it, and the Completed handler
    // and the Begin failure path both clear it. Teardown has to be able to
    // stop it, and its own Completed handler checks it to find out whether it
    // is still the switch the coordinator cares about. See CancelSwitch.
    private Storyboard? _switchStoryboard;
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

    public LayoutCoordinator(
        ColumnDefinition stripColumn,
        ColumnDefinition titleBarStripMirror,
        VerticalTabHost verticalTabHost,
        Grid verticalTitleBar,
        ITabHost horizontalTabHost,
        Canvas morphLayer,
        FrameworkElement morphRoot,
        FrameworkElement paneHost,
        Func<TabModel?> activeTab,
        Action<double, double, TimeSpan>? impact = null,
        Func<bool>? motionEnabled = null)
    {
        _motionEnabled = motionEnabled;
        _impact = impact;
        _horizontalTabHost = horizontalTabHost;
        _morphLayer = morphLayer;
        _morphRoot = morphRoot;
        _paneHost = paneHost;
        _activeTab = activeTab;
        _stripColumn = stripColumn;
        _titleBarStripMirror = titleBarStripMirror;
        _horizontalHost = horizontalTabHost.HostElement;
        _verticalTabHost = verticalTabHost;
        _verticalHost = verticalTabHost;
        _verticalTitleBar = verticalTitleBar;
        _horizontalIcon = horizontalTabHost.IconBadge;
        _verticalIcon = verticalTabHost.IconBadge;

        // Pin toggle snaps immediately -- NavView needs the full column width
        // before IsPaneOpen sticks; a tween left MUXC auto-closing the pane.
        _verticalTabHost.StripWidthChangeRequested += (_, width) =>
            SnapStripColumn(width);
    }

    public bool IsSwitching => _switching;

    /// <summary>
    /// What is parked on the morph layer right now: the active-tab ghost,
    /// the icon stand-in, and the run label the strips share it with. The
    /// same count MorphTrace prints as <c>ghosts=</c>, exposed so the
    /// filmstrip can assert per frame rather than only at the end.
    /// </summary>
    public int TestSeamMorphLayerCount => _morphLayer.Children.Count;

    /// <summary>
    /// Snap both hosts and the vertical title bar to the end state
    /// for <paramref name="verticalTabs"/>. Used at construction
    /// (no animation needed) and from the Storyboard Completed
    /// handler to guarantee a consistent end state regardless of
    /// mid-flight cancellation.
    /// </summary>
    public void Snap(bool verticalTabs)
    {
        // Snap is the end-state authority for interrupted switches too:
        // SetStripHidden and SuppressVerticalTitleBar call it directly
        // mid-flight, so everything a switch keeps in the air -- the tab
        // ghost, the icon stand-in, the pane reveal's margin and clip --
        // has to come down here, not only in FinishSwitch.
        FinishActiveTabMorph();
        FinishIconGhost();
        FinishPaneReveal();

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
        // Two reasons to land without moving, and they share an exit
        // because the answer is the same: the end state, now, correct.
        //
        // The quake window has no strip to fly. And a user who has turned
        // Windows animation effects off, or is in High Contrast, has asked
        // for no motion -- which means none, not a shorter version of it.
        // The morph, the icon spin, the pane reveal and the window nudge
        // are all motion; Snap is the end-state authority and runs every
        // teardown the flourishes would have needed.
        if (_stripHidden || _motionEnabled?.Invoke() == false)
        {
            Snap(verticalTabs);
            onCompleted?.Invoke();
            return;
        }
        if (_switching) return;
        _switching = true;
        // Every trace line this switch emits is stamped off one clock, so
        // the log reads as a budget rather than a list of events.
        _switchClock = Stopwatch.StartNew();
        MorphTrace($"SWITCH begin vertical={verticalTabs}");

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
        MorphTrace("SWITCH lane");

        // The direction the ghost will travel, remembered for whichever
        // path ends up staging the flight: the morph may be staged now or
        // a frame or two later, and only the staging site knows how much
        // of the switch is left to delay the landing by.
        _impactDirection = verticalTabs
            ? new Point(-1, 0)
            : new Point(0, -1);

        // Staged before any transform is applied below. TransformToVisual
        // reads whatever offset the strip is already carrying, so measuring
        // after the incoming translate would aim the ghost at the strip's
        // pre-animation position and leave it EmergeTravel short of home.
        PrepareActiveTabMorph(verticalTabs, incomingOffset);
        MorphTrace("SWITCH staged");

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

        _switchStoryboard = sb;
        sb.Completed += (_, _) =>
        {
            // A cancelled switch must not land; see CancelSwitch for why the
            // landing is the hazard, and why stopping the storyboard alone
            // does not settle it.
            if (!ReferenceEquals(_switchStoryboard, sb)) return;

            // Cleared here rather than in FinishSwitch: FinishSwitch invokes
            // onCompleted, which legitimately stages the next switch when a
            // layout change arrived mid-flight, and clearing after that would
            // null the storyboard that switch just registered. Left set, the
            // field outlives the switch by the life of the window, and
            // CancelSwitch would Stop a long-finished storyboard -- releasing
            // its hold values across the whole chrome tree during teardown,
            // which is the one thing that method exists to avoid.
            _switchStoryboard = null;

            // The impact is not raised here any more. It is scheduled when
            // the ghost's flight is staged, so the compositor can land it
            // with the motion instead of whenever this handler is finally
            // pumped; see the _impact field.
            FinishSwitch(verticalTabs, onCompleted);
        };

        // If Begin throws, Completed never fires and _switching stays
        // latched -- every later layout toggle (keybind, palette, settings)
        // would silently no-op for the life of the window. Land the switch
        // without the animation instead.
        try
        {
            sb.Begin();
            MorphTrace("SWITCH running");
            StartFrameCount();
        }
        catch (Exception)
        {
            // Nothing is in flight after a Begin that threw, and FinishSwitch
            // below can stage the next switch through onCompleted.
            _switchStoryboard = null;
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
        internal EventHandler<object>? WaitingDeadline { get; set; }
    }

    private ActiveTabMorph? _morph;
    private Storyboard? _morphStoryboard;

    /// <summary>Unit direction of the flight being staged.</summary>
    private Point _impactDirection;

    /// <summary>
    /// Oracle for the morph fuzz harness: every switch must end with zero
    /// ghosts on the overlay. Emitting the trace from the product keeps the
    /// harness runnable against any build, and the env var doubles as the
    /// per-run log path so concurrent instances never interleave one file.
    /// Inert (a null check) when the variable is unset.
    /// </summary>
    private static readonly string? MorphTracePath =
        Environment.GetEnvironmentVariable("WINTTY_MORPH_TRACE");

    /// <summary>
    /// Elapsed since the running switch began, for the trace stamps. Null
    /// outside a switch, which is why the stamp reads "--" there.
    /// </summary>
    private Stopwatch? _switchClock;

    // Rendered frames the UI thread produced during the last flight. A
    // 340ms switch that lands at 600ms is either a long animation or a
    // starved thread, and only the frame count tells the two apart.
    private int _uiFrames;
    private EventHandler<object>? _frameCounter;

    private void StartFrameCount()
    {
        if (MorphTracePath is null) return;
        StopFrameCount();
        _uiFrames = 0;
        _frameCounter = (_, _) => _uiFrames++;
        CompositionTarget.Rendering += _frameCounter;
    }

    private int StopFrameCount()
    {
        if (_frameCounter is not null)
        {
            CompositionTarget.Rendering -= _frameCounter;
            _frameCounter = null;
        }
        return _uiFrames;
    }

    private void MorphTrace(string message)
    {
        if (MorphTracePath is null) return;
        try
        {
            var at = _switchClock is { } clock
                ? clock.ElapsedMilliseconds.ToString() + "ms"
                : "--";
            System.IO.File.AppendAllText(
                MorphTracePath, at + " " + message + Environment.NewLine);
        }
        catch
        {
            // A locked or unwritable log must never take the switch down.
        }
    }

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
            _primingFrame = null;
            // Priming owns the host only while it is still the transparent
            // stand-in it created. A switch in flight, or one that already
            // landed on this host (Snap sets its opacity back to 1), means
            // the layout machinery owns visibility now.
            if (_switching || host.Opacity != 0) return;
            host.Visibility = Visibility.Collapsed;
        };
        _primingFrame = onFrame;
        CompositionTarget.Rendering += onFrame;
    }

    /// <summary>
    /// Detach the priming frame handler on window teardown.
    /// CompositionTarget.Rendering is a thread-level event, so a pending
    /// handler would keep the closed window's whole tree alive until the
    /// thread renders again.
    /// </summary>
    public void CancelStripPriming()
    {
        if (_primingFrame is null) return;
        CompositionTarget.Rendering -= _primingFrame;
        _primingFrame = null;
    }

    private EventHandler<object>? _primingFrame;

    /// <summary>
    /// Drop an in-flight layout switch on window teardown without running any
    /// of the end-state work it was going to run.
    ///
    /// A switch lands on its Storyboard's Completed handler roughly 340ms
    /// after it starts, and the landing goes through Snap, which touches both
    /// tab hosts, the vertical title bar and the pane host. A closing window
    /// is disposing exactly that tree, so the landing has to be dropped rather
    /// than fast-forwarded: calling FinishSwitch here would run the work this
    /// method exists to prevent. This is the one place that reasoning is
    /// written down; the Completed handler, the window's completion callback
    /// and the wiring tests point here rather than repeat it.
    ///
    /// Two defences against the landing, because they cover different things.
    /// Stopping the storyboard means Completed is never raised at all;
    /// clearing the field the handler checks its identity against covers a
    /// Completed that was already queued in the same frame as the Stop, which
    /// the Stop cannot recall.
    ///
    /// Then release the rest of what a switch has in the air, none of which
    /// the switch Storyboard drives:
    ///
    /// - The pane reveal is a Composition InsetClip on the pane host's visual
    ///   with a key-frame animation sweeping its left inset, plus a shifted
    ///   margin. The compositor keeps driving that against the pane host while
    ///   the window goes on to dispose its leaves and its host: the same leak,
    ///   reached through the composition tree instead of a XAML event.
    /// - The icon ghost is an Image parked on the morph layer, with both real
    ///   badges left at Opacity 0 behind it.
    /// - A morph still waiting for its destination holds a LayoutUpdated
    ///   handler on the morph root and a deadline on
    ///   CompositionTarget.Rendering. Rendering is a thread-level event, so a
    ///   pending handler keeps the closed window's whole tree alive until the
    ///   thread renders again -- the leak CancelStripPriming exists to close,
    ///   reachable by a second route.
    ///
    /// CancelPaneReveal and FinishIconGhost are safe here where FinishSwitch
    /// is not: between them they touch the pane host's clip, the morph layer
    /// and the Opacity of the two icon badges, none of it disposed at this
    /// point and none of it a call into a tab host, the theme manager or
    /// libghostty. The reveal is released through CancelPaneReveal rather
    /// than FinishPaneReveal because only the clip has to go: putting the
    /// pane host's margin back invalidates measure on a tree whose panes are
    /// about to be freed, to restore a layout nobody will see. The morph's own
    /// restoration is deliberately skipped -- FinishActiveTabMorph puts
    /// opacity back on tab elements nobody will see again and asks the
    /// vertical host to unsuppress its selection row, which is the tree-walking
    /// this avoids. Only its handlers come off.
    ///
    /// Leaves _switching latched true, which is load-bearing rather than
    /// merely harmless: between here and the config/settings unsubscribes
    /// further down the closing path, a settings toggle or a debounced config
    /// reload can still reach the window's layout entry point, and the latch is
    /// what parks it as a pending target instead of starting a switch on a
    /// window that is closing.
    /// </summary>
    public void CancelSwitch()
    {
        if (_switchStoryboard is not null)
        {
            // Pairs with the SWITCH begin line this switch emitted: the fuzz
            // harness counts begins against ends, and a cancelled switch never
            // reaches FinishSwitch to emit an end. No ghost count on the line
            // -- the harness reads any ghosts= above zero as a leak, and a
            // cancel deliberately leaves the morph ghost on a tree that is
            // about to be destroyed.
            MorphTrace("SWITCH cancel");
            _switchStoryboard.Stop();
            _switchStoryboard = null;
        }

        FinishIconGhost();
        CancelPaneReveal();

        _morphStoryboard?.Stop();
        _morphStoryboard = null;
        if (_morph is not { } morph) return;
        // The ghost's box rides the compositor, which no Storyboard.Stop
        // reaches -- the same shape as the pane reveal's sweep, and
        // released here for the same reason.
        morph.Ghost.StopBoxAnimations();
        if (morph.Waiting is not null)
        {
            _morphRoot.LayoutUpdated -= morph.Waiting;
            morph.Waiting = null;
        }
        if (morph.WaitingDeadline is not null)
        {
            CompositionTarget.Rendering -= morph.WaitingDeadline;
            morph.WaitingDeadline = null;
        }
        _morph = null;
    }

    /// <summary>
    /// A realized element can still sit scrolled out of the strip's
    /// viewport; TransformToVisual happily reports its off-screen rect and
    /// the overlay canvas never clips, so a morph aimed there would fly
    /// across the terminal to a point where no tab is visible. Off-viewport
    /// rects count as unavailable and the switch degrades to the
    /// cross-fade.
    /// </summary>
    private static Rect ViewportRectIn(FrameworkElement root, FrameworkElement el)
    {
        var rect = RectIn(root, el);
        if (rect.Width <= 0) return rect;
        var overlap = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        overlap.Intersect(rect);
        return overlap.IsEmpty ? default : rect;
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
        var fromRect = ViewportRectIn(_morphRoot, from);
        if (fromRect.Width <= 0) return;

        var (to, toRect) = MeasureIncomingTab(incoming, tab);

        var chrome = _verticalTabHost.ActiveRowChrome(tab);
        // Rail rows are square and meet the pane edge-to-edge; header tabs
        // keep the tab-like rounding on their top corners only.
        var destinationShape = verticalTabs
            ? new CornerRadius(0)
            : new CornerRadius(4, 4, 0, 0);
        var ghost = new TabMorphGhost(tab, chrome.Fill, chrome.Foreground, destinationShape);
        // Sized by TryComposeBox when the destination is known, and by the
        // caller's fallback when it is not: a ghost that is still waiting
        // for its landing rect (see below) has nowhere to aim yet, so it
        // holds the source box until one arrives.
        ghost.ResizeForFallback(fromRect.Width, fromRect.Height);
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
            MorphTrace("MORPH immediate");
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
            var rect = late is null ? default : ViewportRectIn(_morphRoot, late);

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
            MorphTrace($"MORPH deferred@{clock.ElapsedMilliseconds}ms");
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
        MorphTrace("MORPH waiting");

        // LayoutUpdated alone cannot enforce the grace deadline: the switch
        // storyboard animates only opacity and transforms, which dirty no
        // layout, so once the visibility-change layout storm settles no
        // further LayoutUpdated is guaranteed. A container that never
        // realizes would park the ghost for the whole switch with both real
        // tabs hidden under it. Rendered frames keep coming regardless, so
        // they carry the deadline.
        EventHandler<object>? deadline = null;
        deadline = (_, _) =>
        {
            if (!ReferenceEquals(_morph, morph) || morph.Waiting is null)
            {
                CompositionTarget.Rendering -= deadline;
                return;
            }
            if (clock.Elapsed <= RealizationGrace) return;
            CompositionTarget.Rendering -= deadline;
            morph.WaitingDeadline = null;
            FinishActiveTabMorph();
        };
        morph.WaitingDeadline = deadline;
        CompositionTarget.Rendering += deadline;
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
        var toRect = to is null ? default : ViewportRectIn(_morphRoot, to);
        return (to, toRect);
    }

    /// <summary>
    /// Share of the flight the ghost spends changing size, so it covers
    /// the last stretch already in its destination shape and the landing
    /// is a pure glide instead of arrive-then-resize.
    /// </summary>
    private const double ShapeSettleFraction = 0.75;

    /// <summary>
    /// Share of the flight the ghost spends TRAVELLING, and it is short
    /// on purpose: the incoming strip arrives with a hole where the
    /// active tab belongs, and the ghost is what fills it. Filmed against
    /// the old timing -- full duration, eased in AND out -- the ghost was
    /// a quarter of the way home a third of the way through, so the hole
    /// stood open for most of the switch with a small chip floating in the
    /// terminal a hundred pixels below it. Settling early, on a curve that
    /// spends its speed at the start, puts the tab in its slot at about
    /// the moment the strip around it becomes readable.
    ///
    /// Trimmed from 0.85 when the incoming fade was pulled forward: the
    /// two have to arrive together, and moving one without the other just
    /// relocates the mismatch.
    /// </summary>
    private const double PositionSettleFraction = 0.78;

    /// <summary>
    /// Share of the flight the ghost's LABEL has to get out of the way in.
    ///
    /// Separate from the shape settle, and shorter, because the label is
    /// the thing that makes a ghost read as a tab rather than as a moving
    /// shape. Filmed crossing the terminal towards an empty rail, a fully
    /// legible tab reads as something being dragged; the same rectangle
    /// with its text gone reads as chrome rearranging itself, which is
    /// what is actually happening. It leaves on the same curve the
    /// outgoing strip does, and for the same reason.
    /// </summary>
    private const double LabelSettleFraction = 0.45;

    /// <summary>
    /// Drive the ghost from one rect to the other.
    ///
    /// Position is a Storyboard on the ghost's TranslateTransform, which
    /// XAML runs independently -- the compositor carries it whatever the
    /// UI thread is doing, and it is the half of the motion the eye reads
    /// as travel.
    ///
    /// The BOX is handed to the compositor outright (see
    /// <see cref="TabMorphGhost.TryComposeBox"/>). It used to be a pair of
    /// dependent Width/Height animations, which is to say a relayout per
    /// frame on the UI thread -- the one thread a terminal's own render
    /// owns, and which was measured producing three to thirteen frames
    /// across a whole switch. Only when composition refuses does the old
    /// tween run, at whatever frame rate the thread can spare.
    /// </summary>
    private void StageMorphAnimations(
        ActiveTabMorph morph, Rect from, Rect to, TimeSpan duration)
    {
        var settle = duration * ShapeSettleFraction;
        var travel = duration * PositionSettleFraction;
        var labelSpan = duration * LabelSettleFraction;
        var sb = new Storyboard();
        Add(morph.Ghost.Translate, "X", from.X, to.X, span: travel, arriving: true);
        Add(morph.Ghost.Translate, "Y", from.Y, to.Y, span: travel, arriving: true);
        if (!morph.Ghost.TryComposeBox(
                new Windows.Foundation.Size(from.Width, from.Height),
                new Windows.Foundation.Size(to.Width, to.Height),
                settle))
        {
            Add(morph.Ghost, "Width", from.Width, to.Width, dependent: true, settle);
            Add(morph.Ghost, "Height", from.Height, to.Height, dependent: true, settle);
        }

        // The label survives a modest width change but not the collapse to
        // a 48px rail, so it fades on the leg that loses most of its room
        // and fades back in on the return leg.
        var shrinking = to.Width < from.Width * LabelFadeRatio;
        var growing = from.Width < to.Width * LabelFadeRatio;
        if (shrinking || growing)
        {
            morph.Ghost.Label.Opacity = shrinking ? 1 : 0;
            // Leaving is arriving's mirror here: a label on its way out
            // spends its speed at the start so the ghost stops being a
            // legible tab early, and one on its way in takes the same
            // curve so it is readable before the landing rather than at
            // it.
            Add(morph.Ghost.Label, "Opacity",
                shrinking ? 1 : 0, shrinking ? 0 : 1,
                dependent: false, labelSpan, arriving: true);
        }

        _morphStoryboard = sb;

        // Scheduled from here rather than from the switch's completion, and
        // from here rather than from Animate, because this is the one place
        // that knows the flight is really happening AND how long is left of
        // it: a deferred morph stages with only the remainder of the switch
        // to run, and the landing has to be delayed by that much and no
        // more.
        _impact?.Invoke(_impactDirection.X, _impactDirection.Y, duration);

        void Add(
            DependencyObject target, string path,
            double f, double t, bool dependent = false, TimeSpan? span = null,
            bool arriving = false)
        {
            var anim = new DoubleAnimation
            {
                From = f,
                To = t,
                Duration = new Duration(span ?? duration),
                EnableDependentAnimation = dependent,
                // Travel eases OUT: the ghost is arriving somewhere, and
                // arrivals spend their speed at the start and settle. The
                // rest (size, label) keeps the symmetric curve, which is
                // right for a property that is only changing shape rather
                // than going anywhere.
                EasingFunction = arriving
                    ? new CubicEase { EasingMode = EasingMode.EaseOut }
                    : new CubicEase { EasingMode = EasingMode.EaseInOut },
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
        if (_morph.WaitingDeadline is not null)
        {
            CompositionTarget.Rendering -= _morph.WaitingDeadline;
            _morph.WaitingDeadline = null;
        }
        _morph.Ghost.StopBoxAnimations();
        _morphLayer.Children.Remove(_morph.Ghost);
        if (_morph.From is not null) _morph.From.Opacity = 1;
        if (_morph.To is not null) _morph.To.Opacity = 1;
        _verticalTabHost.SetSelectionRowSuppressed(false);
        _morph = null;
    }

    private void FinishSwitch(bool verticalTabs, Action? onCompleted)
    {
        if (!_switching) return;
        // Snap tears down the in-flight morph, icon ghost, and pane reveal
        // itself, so the direct-interrupt callers get the same cleanup.
        Snap(verticalTabs);
        MorphTrace(
            $"SWITCH end ghosts={_morphLayer.Children.Count} morph={(_morph is null ? "null" : "LEAKED")} uiFrames={StopFrameCount()}");
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
        // Arriving mid-switch (chevron and pin stay clickable during the
        // 340ms flight), a width change invalidates every rect the morph
        // measured; drop the flourishes and let the cross-fade finish.
        FinishActiveTabMorph();
        FinishIconGhost();
        FinishPaneReveal();
        _stripColumn.Width = new GridLength(width);
        _titleBarStripMirror.Width = new GridLength(width);
        _verticalTabHost.SetInternalStripWidth(width);
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

        sb.Children.Add(MakeRotateAnim(rotate, 0, spin));
        sb.Children.Add(MakePopAnim(scale, "ScaleX"));
        sb.Children.Add(MakePopAnim(scale, "ScaleY"));
        return true;
    }

    private void FinishIconGhost()
    {
        if (_iconGhost is null) return;
        _morphLayer.Children.Remove(_iconGhost);
        _iconGhost = null;
        // The hidden pair is always the two badge fields, whichever
        // direction flew.
        _horizontalIcon.Opacity = 1;
        _verticalIcon.Opacity = 1;
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
            var saved = _paneHost.Margin;
            _savedPaneMargin = saved;
            _paneHost.Margin = new Thickness(
                saved.Left - laneWidth,
                saved.Top,
                saved.Right,
                saved.Bottom);
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
        if (_savedPaneMargin is not { } saved) return;
        _savedPaneMargin = null;
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
            .GetElementVisual(_paneHost).Clip = null;
        _paneHost.Margin = saved;
    }

    /// <summary>
    /// Release the reveal on a window that is closing: drop the clip, leave
    /// the margin shifted.
    ///
    /// The clip is the half that has to go. Its left inset is swept by a
    /// key-frame animation the compositor owns, so no Storyboard.Stop reaches
    /// it and it would go on running against the pane host after the window
    /// is gone. Restoring the margin is the half that must not run: it is
    /// cosmetic on a window nobody will see again, and writing it invalidates
    /// measure on a tree whose panes are about to be freed, which is the work
    /// CancelSwitch exists to avoid.
    ///
    /// Guarded the way StartPaneReveal guards the same composition calls, and
    /// for a second reason here: the only caller runs before the first await
    /// of the window's async void Closed handler, where a COM teardown race
    /// would come back as an unhandled exception on the UI thread.
    /// </summary>
    private void CancelPaneReveal()
    {
        if (_savedPaneMargin is null) return;
        _savedPaneMargin = null;
        try
        {
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                .GetElementVisual(_paneHost).Clip = null;
        }
        catch (Exception)
        {
            // Composition refused, or the visual is already gone. Either way
            // there is nothing left to release.
        }
    }

    /// <summary>Non-null exactly while a pane reveal is in flight.</summary>
    private Thickness? _savedPaneMargin;

    private Image? _iconGhost;

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
            // EaseOut on a value falling to zero means it drops hardest
            // first and tails off -- the strip commits to leaving in the
            // opening frames instead of loitering. See the fade constants
            // above for the measurements that made this the wrong way
            // round before.
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
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
