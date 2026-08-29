namespace Ghostty.Core.Tabs;

/// <summary>
/// The horizontal group run label's geometry and show/hide rules, in the
/// one place that needs no WinUI host to pin by test.
///
/// The label is visual sugar over an expanded run: the rail carries
/// no inline name, so the name floats above the run instead. The element
/// that renders it lives in the shell; everything about WHERE it sits and
/// WHEN it shows is here, so the rules are executable without a strip --
/// the same split TabChipDrop uses for the drop map.
/// </summary>
public static class TabRunLabelShape
{
    /// <summary>The label's fixed height. Content never grows it.</summary>
    public const double HeightPx = 24;

    /// <summary>
    /// Clearance between the label's bottom edge and the rail line: the
    /// label floats 4px above the run's 2px top rail, close enough to read
    /// as belonging to the run, far enough not to touch it.
    /// </summary>
    public const double RailGapPx = 4;

    /// <summary>
    /// The group title ellipsizes past this rather than stretching the
    /// label; the run's own width caps it again from below.
    /// </summary>
    public const double TitleMaxWidthPx = 240;

    /// <summary>Hover shows the label after the classic TTDT_INIT delay.</summary>
    public const int HoverShowMs = 500;

    /// <summary>
    /// Pointer-out grace: moving between a run's own members exits one tab
    /// and enters the next, and the label must not flicker across the gap.
    /// </summary>
    public const int LeaveGraceMs = 150;

    /// <summary>
    /// A keyboard show (selection landing on a grouped member) is a
    /// courtesy, not a hover: it shows for this long, then fades itself.
    /// </summary>
    public const int KeyboardShowMs = 1200;

    /// <summary>The Fade token: 83ms linear in and out.</summary>
    public const int FadeMs = 83;

    /// <summary>
    /// Where the label sits for a run occupying <paramref name="runLeft"/>,
    /// <paramref name="runTop"/>, <paramref name="runWidth"/> in the host
    /// surface's own coordinates. Left-aligned to the run's first member
    /// and never wider than the run -- a label wider than what it names
    /// reads as pointing at the neighbours. The title ellipsizes within.
    /// </summary>
    public static (double Left, double Top, double Width) Place(
        double runLeft, double runTop, double runWidth)
    {
        var width = double.IsFinite(runWidth) && runWidth > 0 ? runWidth : 0;
        return (runLeft, runTop - RailGapPx - HeightPx, width);
    }

    /// <summary>
    /// The fade duration for one transition. Motion on, the Fade token;
    /// motion off, zero -- a cut. State never waits on the animation
    /// completing either way (the caller collapses on completion, so a
    /// zero duration is simply the same path one frame later).
    /// </summary>
    public static TimeSpan FadeDuration(bool motionOn)
        => TimeSpan.FromMilliseconds(motionOn ? FadeMs : 0);
}

/// <summary>
/// The label's show/hide rule machine, one instance per horizontal strip.
/// Pure: events go in, a phase comes out, and the host translates phase
/// changes into element ops and timer arms. It holds no timers itself --
/// Core has no dispatcher -- so every arm is the host's doing and a missed
/// arm is visible in review rather than swallowed in a callback.
/// </summary>
public sealed class TabRunLabelRules
{
    /// <summary>
    /// Idle = hidden and nothing pending. HoverPending = the show delay is
    /// running. Shown = visible. GracePending = the pointer just left and
    /// the grace is running.
    /// </summary>
    public enum Phase { Idle, HoverPending, Shown, GracePending }

    public Phase Current { get; private set; } = Phase.Idle;

    /// <summary>
    /// A drag is live anywhere that matters. While it is, hover and
    /// keyboard shows are refused -- and the hide that ended whatever was
    /// showing was a cut, so the label can never overlap the drag ghost.
    /// </summary>
    public bool DragLive { get; private set; }

    /// <summary>
    /// The most recent hide was demanded as a cut (a drag start), until
    /// the drag ends. The host reads this at the same instant it reads
    /// Current, so it can only matter on a transition into Idle.
    /// </summary>
    public bool CutOnHide { get; private set; }

    /// <summary>
    /// The current showing was requested by the keyboard rule, not hover:
    /// the host arms its auto-hide for that case and not the hover one.
    /// </summary>
    public bool KeyboardShown { get; private set; }

    /// <summary>Pointer entered a run's member.</summary>
    public Phase HoverEnter()
    {
        if (DragLive) return Current;
        KeyboardShown = false;
        Current = Current == Phase.Shown ? Phase.Shown : Phase.HoverPending;
        return Current;
    }

    /// <summary>Pointer left a run's member.</summary>
    public Phase HoverExit()
    {
        if (DragLive) return Current;
        Current = Current switch
        {
            Phase.Shown => Phase.GracePending,
            Phase.HoverPending => Phase.Idle,
            _ => Current,
        };
        return Current;
    }

    /// <summary>The show delay fired.</summary>
    public Phase HoverTimerFired()
    {
        if (DragLive) { Current = Phase.Idle; return Current; }
        if (Current == Phase.HoverPending) Current = Phase.Shown;
        return Current;
    }

    /// <summary>The pointer-out grace fired.</summary>
    public Phase GraceTimerFired()
    {
        if (Current == Phase.GracePending) Current = Phase.Idle;
        return Current;
    }

    /// <summary>
    /// Selection landed on a grouped member of an expanded run. This is
    /// the keyboard rule: show now, auto-hide after KeyboardShowMs. A
    /// selection change is also a hide rule for whatever showed before --
    /// the host hides the old run's label on the same event before
    /// consulting this one.
    /// </summary>
    public Phase KeyboardRequested()
    {
        if (DragLive) return Current;
        KeyboardShown = true;
        Current = Phase.Shown;
        return Current;
    }

    /// <summary>The keyboard auto-hide fired.</summary>
    public Phase KeyboardTimerFired()
    {
        if (Current == Phase.Shown && KeyboardShown) Current = Phase.Idle;
        return Current;
    }

    /// <summary>
    /// A drag started anywhere in the strip -- either strip: the hide is
    /// part of the drag start's own dispatch pass, which is what keeps
    /// the label from ever overlapping the drag ghost. Everything pending
    /// dies here; nothing defers it.
    /// </summary>
    public Phase DragStarting()
    {
        DragLive = true;
        CutOnHide = true;
        KeyboardShown = false;
        Current = Phase.Idle;
        return Current;
    }

    /// <summary>The drag ended. Hover may show again.</summary>
    public Phase DragEnded()
    {
        DragLive = false;
        CutOnHide = false;
        return Current;
    }

    /// <summary>The showing group collapsed.</summary>
    public Phase Collapsed() => EndShow();

    /// <summary>The selection moved.</summary>
    public Phase SelectionChanged() => EndShow();

    /// <summary>A layout switch was requested.</summary>
    public Phase LayoutSwitchRequested() => EndShow();

    /// <summary>The window deactivated.</summary>
    public Phase Deactivated() => EndShow();

    private Phase EndShow()
    {
        KeyboardShown = false;
        Current = Phase.Idle;
        return Current;
    }
}
