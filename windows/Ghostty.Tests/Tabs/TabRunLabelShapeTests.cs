using System;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The run label's geometry and rule machine, without a strip. The shell
/// renders what this answers; every regression that would show up as a
/// misplaced popup, a stuck label, or an 83ms fade over a drag ghost is a
/// wrong answer HERE first.
/// </summary>
public sealed class TabRunLabelShapeTests
{
    [Fact]
    public void The_spec_numbers_are_the_label_numbers()
    {
        // The label contract: 24px tall, 4px above the rail, title ellipsized at
        // 240, the classic TTDT_INIT hover delay, the anti-flicker grace,
        // the keyboard courtesy, and the Fade token. A number drifting
        // here drifts on screen; pin them as the contract they are.
        Assert.Equal(24, TabRunLabelShape.HeightPx);
        Assert.Equal(4, TabRunLabelShape.RailGapPx);
        Assert.Equal(240, TabRunLabelShape.TitleMaxWidthPx);
        Assert.Equal(500, TabRunLabelShape.HoverShowMs);
        Assert.Equal(150, TabRunLabelShape.LeaveGraceMs);
        Assert.Equal(1200, TabRunLabelShape.KeyboardShowMs);
        Assert.Equal(83, TabRunLabelShape.FadeMs);
    }

    [Fact]
    public void The_label_floats_four_above_the_rail_left_aligned_to_the_run()
    {
        // A run whose head sits at (120, 100), 300 wide: the label's
        // bottom edge clears the rail line by exactly the gap, and its
        // left edge is the run's first member -- never the strip's.
        var (left, top, width) = TabRunLabelShape.Place(120, 100, 300);
        Assert.Equal(120, left);
        Assert.Equal(100 - TabRunLabelShape.RailGapPx - TabRunLabelShape.HeightPx, top);
        Assert.Equal(300, width);
    }

    [Fact]
    public void The_label_is_never_wider_than_the_run_names()
    {
        // A run with no arranged bounds places a zero-width label: the
        // host refuses to show, it does not fall back to a guess.
        var (_, _, width) = TabRunLabelShape.Place(0, 50, -5);
        Assert.Equal(0, width);
    }

    [Fact]
    public void Motion_off_renders_the_cut_motion_on_the_fade()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(83), TabRunLabelShape.FadeDuration(true));
        // Zero, not a short fade: under the motion gate the label lands
        // in the same pass, the same path a cut takes.
        Assert.Equal(TimeSpan.Zero, TabRunLabelShape.FadeDuration(false));
    }

    // --- The rule machine ---

    [Fact]
    public void Hover_shows_after_the_delay_and_hides_after_the_grace()
    {
        var rules = new TabRunLabelRules();
        Assert.Equal(TabRunLabelRules.Phase.Idle, rules.Current);

        Assert.Equal(TabRunLabelRules.Phase.HoverPending, rules.HoverEnter());
        Assert.Equal(TabRunLabelRules.Phase.Shown, rules.HoverTimerFired());

        // Out of the run: the grace is the phase between shown and gone.
        Assert.Equal(TabRunLabelRules.Phase.GracePending, rules.HoverExit());
        Assert.Equal(TabRunLabelRules.Phase.Idle, rules.GraceTimerFired());
    }

    [Fact]
    public void Crossing_between_members_of_one_run_never_passes_through_idle()
    {
        var rules = new TabRunLabelRules();
        rules.HoverEnter();
        rules.HoverTimerFired();

        // Head to neighbour: out of one member, into the next. Neither
        // transition may land on Idle -- Idle hides the label, and the
        // gap between two members of the run the label is naming is not
        // a leave. The grace absorbs the exit; the re-entry supersedes it.
        var afterExit = rules.HoverExit();
        Assert.Equal(TabRunLabelRules.Phase.GracePending, afterExit);
        var afterEnter = rules.HoverEnter();
        Assert.NotEqual(TabRunLabelRules.Phase.Idle, afterEnter);
        Assert.NotEqual(TabRunLabelRules.Phase.Idle, afterExit);
    }

    [Fact]
    public void A_drag_start_is_a_cut_in_the_same_pass_from_any_phase()
    {
        // Pending, shown, or mid-grace: the drag start ends the label
        // outright, no grace, no delay -- the hide rides the same dispatch
        // pass that lifts the ghost, which is the rule's whole point.
        foreach (var warmup in new Func<TabRunLabelRules, TabRunLabelRules.Phase>[] {
            r => { r.HoverEnter(); return r.Current; },
            r => { r.HoverEnter(); r.HoverTimerFired(); return r.Current; },
            r => { r.HoverEnter(); r.HoverTimerFired(); r.HoverExit(); return r.Current; },
        })
        {
            var rules = new TabRunLabelRules();
            warmup(rules);
            Assert.NotEqual(TabRunLabelRules.Phase.Idle, rules.Current);

            Assert.Equal(TabRunLabelRules.Phase.Idle, rules.DragStarting());
            Assert.True(rules.CutOnHide, "the drag-start hide must be a cut");
            Assert.True(rules.DragLive);
        }

        // The cut demand and the drag live past the drag; an ordinary
        // later hide must be a fade, not the drag's cut leaking.
        var after = new TabRunLabelRules();
        after.DragStarting();
        after.DragEnded();
        Assert.False(after.DragLive);
        Assert.False(after.CutOnHide);
    }

    [Fact]
    public void Under_a_drag_no_rule_shows_the_label()
    {
        var rules = new TabRunLabelRules();
        rules.DragStarting();

        // Hover and keyboard alike are refused while a drag is live: the
        // strip is being reordered under the pointer, and a run's
        // position is exactly what is not settled.
        Assert.Equal(TabRunLabelRules.Phase.Idle, rules.HoverEnter());
        Assert.Equal(TabRunLabelRules.Phase.Idle, rules.KeyboardRequested());
        Assert.Equal(TabRunLabelRules.Phase.Idle, rules.HoverTimerFired());
    }

    [Fact]
    public void A_keyboard_show_auto_expires_and_hover_replaces_it()
    {
        var rules = new TabRunLabelRules();
        Assert.Equal(TabRunLabelRules.Phase.Shown, rules.KeyboardRequested());
        Assert.True(rules.KeyboardShown);
        Assert.Equal(TabRunLabelRules.Phase.Idle, rules.KeyboardTimerFired());

        // The next hover is the hover rule, not a keyboard encore: the
        // host arms its 500ms delay, not another 1200ms courtesy.
        rules.KeyboardRequested();
        rules.HoverEnter();
        Assert.False(rules.KeyboardShown);
    }

    [Fact]
    public void The_hide_rules_all_land_on_idle_without_a_cut()
    {
        // Collapse, selection, a layout switch request, deactivation:
        // each ends the label as a plain fade -- the cut is the drag
        // start's alone, and none of these may leave it set.
        Func<TabRunLabelRules, TabRunLabelRules.Phase>[] hides =
        [
            r => r.Collapsed(),
            r => r.SelectionChanged(),
            r => r.LayoutSwitchRequested(),
            r => r.Deactivated(),
        ];
        foreach (var hide in hides)
        {
            var rules = new TabRunLabelRules();
            rules.HoverEnter();
            rules.HoverTimerFired();
            Assert.Equal(TabRunLabelRules.Phase.Idle, hide(rules));
            Assert.False(rules.CutOnHide);
        }
    }
}
