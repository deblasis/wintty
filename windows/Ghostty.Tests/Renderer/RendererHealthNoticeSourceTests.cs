using Ghostty.Core.Notifications;
using Ghostty.Core.Renderer;
using Xunit;

namespace Ghostty.Tests.Renderer;

public class RendererHealthNoticeSourceTests
{
    // Stand-in surface handles. Only their distinctness matters.
    private static readonly nint PaneA = 1;
    private static readonly nint PaneB = 2;

    [Fact]
    public void First_unhealthy_pane_raises_a_closable_warning_with_no_actions()
    {
        var change = new RendererHealthNoticeSource().Update(PaneA, RendererHealth.Unhealthy);

        Assert.NotNull(change.Show);
        Assert.Null(change.Dismiss);
        Assert.Equal("Graphics device lost", change.Show!.Title);
        Assert.Equal(NoticeSeverity.Warning, change.Show.Severity);
        Assert.Equal(RendererHealthNoticeSource.DedupKey, change.Show.DedupKey);
        Assert.Empty(change.Show.Actions);
        Assert.True(change.Show.IsClosable);
        Assert.NotEqual(string.Empty, change.Show.Message);
    }

    [Fact]
    public void A_healthy_report_from_a_pane_that_was_never_broken_says_nothing()
    {
        var change = new RendererHealthNoticeSource().Update(PaneA, RendererHealth.Healthy);

        Assert.Null(change.Show);
        Assert.Null(change.Dismiss);
    }

    [Fact]
    public void Recovery_dismisses_the_banner_that_was_raised()
    {
        var source = new RendererHealthNoticeSource();
        var raised = source.Update(PaneA, RendererHealth.Unhealthy).Show;

        var change = source.Update(PaneA, RendererHealth.Healthy);

        Assert.Same(raised, change.Dismiss);
        Assert.Null(change.Show);
    }

    [Fact]
    public void A_repeated_unhealthy_report_does_not_raise_a_second_banner()
    {
        var source = new RendererHealthNoticeSource();
        Assert.NotNull(source.Update(PaneA, RendererHealth.Unhealthy).Show);

        var again = source.Update(PaneA, RendererHealth.Unhealthy);

        Assert.Null(again.Show);
        Assert.Null(again.Dismiss);
    }

    [Fact]
    public void The_banner_stays_up_until_the_last_broken_pane_recovers()
    {
        // The case a last-one-wins flag gets wrong. A device loss is
        // adapter-wide, so both panes report unhealthy and then recover
        // independently; clearing on the first recovery would take the banner
        // down while the other pane is still frozen.
        var source = new RendererHealthNoticeSource();
        var raised = source.Update(PaneA, RendererHealth.Unhealthy).Show;
        Assert.NotNull(raised);
        Assert.Null(source.Update(PaneB, RendererHealth.Unhealthy).Show);

        Assert.Null(source.Update(PaneA, RendererHealth.Healthy).Dismiss);

        Assert.Same(raised, source.Update(PaneB, RendererHealth.Healthy).Dismiss);
    }

    [Fact]
    public void Forgetting_the_last_broken_pane_takes_the_banner_down()
    {
        // A pane closed while its renderer is down never reports healthy
        // again, so the banner would otherwise outlive everything it refers to.
        var source = new RendererHealthNoticeSource();
        var raised = source.Update(PaneA, RendererHealth.Unhealthy).Show;

        Assert.Same(raised, source.Forget(PaneA).Dismiss);
    }

    [Fact]
    public void Forgetting_a_pane_that_was_fine_says_nothing()
    {
        var source = new RendererHealthNoticeSource();
        var raised = source.Update(PaneA, RendererHealth.Unhealthy).Show;

        var change = source.Forget(PaneB);

        Assert.Null(change.Show);
        Assert.Null(change.Dismiss);

        // And the banner PaneA raised is still the live one.
        Assert.Same(raised, source.Update(PaneA, RendererHealth.Healthy).Dismiss);
    }

    [Fact]
    public void A_later_loss_raises_the_banner_again()
    {
        // Unlike the custom-shader notice there is no once-per-session gate:
        // a device that dies again is a new fact about the machine, not a
        // repeat of a config mistake the user has already been told about.
        var source = new RendererHealthNoticeSource();
        var first = source.Update(PaneA, RendererHealth.Unhealthy).Show;
        source.Update(PaneA, RendererHealth.Healthy);

        var second = source.Update(PaneA, RendererHealth.Unhealthy).Show;

        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Forgetting_one_of_several_broken_panes_leaves_the_banner_up()
    {
        var source = new RendererHealthNoticeSource();
        var raised = source.Update(PaneA, RendererHealth.Unhealthy).Show;
        source.Update(PaneB, RendererHealth.Unhealthy);

        // Closing one broken pane does not mean the others came back.
        var change = source.Forget(PaneA);
        Assert.Null(change.Show);
        Assert.Null(change.Dismiss);

        Assert.Same(raised, source.Forget(PaneB).Dismiss);
    }

    [Fact]
    public void A_banner_the_viewer_closed_does_not_come_back_during_the_same_outage()
    {
        var source = new RendererHealthNoticeSource();
        var raised = source.Update(PaneA, RendererHealth.Unhealthy).Show;
        Assert.NotNull(raised);

        // The close X, which the service reports through OnDismiss.
        raised!.OnDismiss!();

        // Another pane goes dark. They have already been told.
        var change = source.Update(PaneB, RendererHealth.Unhealthy);
        Assert.Null(change.Show);
        Assert.Null(change.Dismiss);
    }

    [Fact]
    public void A_banner_the_viewer_closed_comes_back_for_the_next_outage()
    {
        var source = new RendererHealthNoticeSource();
        var first = source.Update(PaneA, RendererHealth.Unhealthy).Show;
        first!.OnDismiss!();

        // Everything recovers, so the next loss is a new event rather than
        // a repeat of the one they put away.
        source.Update(PaneA, RendererHealth.Healthy);
        var second = source.Update(PaneA, RendererHealth.Unhealthy).Show;

        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Our_own_dismissal_is_not_mistaken_for_the_viewer_closing_it()
    {
        // OnDismiss fires for both. If the source could not tell them apart it
        // would think the viewer had put the banner away, and stay silent for
        // the whole of the next outage.
        var source = new RendererHealthNoticeSource();
        var first = source.Update(PaneA, RendererHealth.Unhealthy).Show;

        var dismiss = source.Update(PaneA, RendererHealth.Healthy).Dismiss;
        Assert.Same(first, dismiss);
        dismiss!.OnDismiss!();

        Assert.NotNull(source.Update(PaneA, RendererHealth.Unhealthy).Show);
    }

    [Fact]
    public void The_copy_never_claims_the_session_was_lost()
    {
        // The shell keeps running and the scrollback is intact through a
        // device loss; copy that implied otherwise would send the user
        // rebuilding a session that never went away.
        var notice = new RendererHealthNoticeSource().Update(PaneA, RendererHealth.Unhealthy).Show;

        Assert.NotNull(notice);
        var text = notice!.Title + " " + notice.Message;
        foreach (var word in new[] { "closed", "lost your", "restart", "reopen" })
        {
            Assert.DoesNotContain(word, text, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_copy_never_promises_the_renderer_comes_back()
    {
        // The rebuilding banner may say it is trying, never that it will
        // succeed. When it does not, the abandoned banner replaces it.
        var notice = new RendererHealthNoticeSource().Update(PaneA, RendererHealth.Unhealthy).Show;

        Assert.NotNull(notice);
        foreach (var phrase in new[] { "until it finishes", "will be back", "shortly" })
        {
            Assert.DoesNotContain(phrase, notice!.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        // And says what it does mean, rather than only avoiding the promise:
        // a message that said nothing at all would pass the check above.
        Assert.Contains("trying to rebuild", notice!.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dropping_an_abandoned_pane_hands_back_the_rebuilding_banner()
    {
        // The case that made Forget able to return a Show for the first time.
        // A caller applying only the Dismiss half here takes the banner away
        // from panes that are still frozen, and then suppresses every later
        // report because the source believes a banner is up.
        var source = new RendererHealthNoticeSource();
        source.Update(PaneB, RendererHealth.Unhealthy);
        var abandoned = source.Update(PaneA, RendererHealth.Abandoned).Show;
        Assert.NotNull(abandoned);

        var change = source.Forget(PaneA);

        Assert.Same(abandoned, change.Dismiss);
        Assert.NotNull(change.Show);
        Assert.NotSame(abandoned, change.Show);
    }

    [Fact]
    public void A_pane_reported_unhealthy_after_abandoned_leaves_the_abandoned_set()
    {
        // The renderer does not walk that transition back today. The sets are
        // kept disjoint by construction rather than by trusting it not to.
        var source = new RendererHealthNoticeSource();
        source.Update(PaneA, RendererHealth.Abandoned);

        source.Update(PaneA, RendererHealth.Unhealthy);

        // If PaneA were still counted as abandoned, this would have nothing
        // to dismiss and the banner would outlive the outage.
        Assert.NotNull(source.Update(PaneA, RendererHealth.Healthy).Dismiss);
    }

    [Fact]
    public void Dismissing_the_abandoned_banner_is_not_undone_by_dropping_back()
    {
        var source = new RendererHealthNoticeSource();
        source.Update(PaneB, RendererHealth.Unhealthy);
        var abandoned = source.Update(PaneA, RendererHealth.Abandoned).Show;
        abandoned!.OnDismiss!();

        // Closing the abandoned pane's tab leaves PaneB still rebuilding.
        // That is weaker news than what they just put away, so it must not
        // come back at them.
        var change = source.Forget(PaneA);
        Assert.Null(change.Show);
    }

    [Fact]
    public void Both_banners_keep_the_session_out_of_it()
    {
        // The shell keeps running through either state. Copy implying
        // otherwise would send the user rebuilding a session that is fine.
        foreach (var health in new[] { RendererHealth.Unhealthy, RendererHealth.Abandoned })
        {
            var notice = new RendererHealthNoticeSource().Update(PaneA, health).Show;
            Assert.NotNull(notice);
            var text = notice!.Title + " " + notice.Message;
            foreach (var word in new[] { "closed", "lost your", "restart" })
            {
                Assert.DoesNotContain(word, text, System.StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void The_abandoned_copy_does_not_call_a_shader_the_likely_cause()
    {
        // The renderer blames a custom-shader for one of its three abandon
        // reasons and refuses to for the other two, and the reason does not
        // cross the ABI. So this can offer the shader as something to try,
        // never as the cause.
        var notice = new RendererHealthNoticeSource().Update(PaneA, RendererHealth.Abandoned).Show;

        Assert.NotNull(notice);
        foreach (var phrase in new[] { "most likely cause", "is why", "because of" })
        {
            Assert.DoesNotContain(phrase, notice!.Message, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void An_abandoned_pane_replaces_the_rebuilding_banner()
    {
        var source = new RendererHealthNoticeSource();
        var rebuilding = source.Update(PaneA, RendererHealth.Unhealthy).Show;
        Assert.NotNull(rebuilding);

        var change = source.Update(PaneA, RendererHealth.Abandoned);

        // Both halves, and the old one must come down: they share a DedupKey,
        // so a Show on its own would be swallowed and the user would go on
        // reading advice that no longer applies.
        Assert.Same(rebuilding, change.Dismiss);
        Assert.NotNull(change.Show);
        Assert.NotSame(rebuilding, change.Show);
    }

    [Fact]
    public void The_abandoned_copy_says_the_pane_is_not_coming_back()
    {
        var notice = new RendererHealthNoticeSource().Update(PaneA, RendererHealth.Abandoned).Show;

        Assert.NotNull(notice);
        var text = notice!.Message;
        Assert.Contains("will not come back", text, System.StringComparison.OrdinalIgnoreCase);
        // And tells them the one thing that actually helps.
        Assert.Contains("new tab", text, System.StringComparison.OrdinalIgnoreCase);
        // Without implying the session died with the pane.
        Assert.Contains("still running", text, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_abandoned_pane_outranks_a_pane_still_being_retried()
    {
        var source = new RendererHealthNoticeSource();
        var abandoned = source.Update(PaneA, RendererHealth.Abandoned).Show;
        Assert.NotNull(abandoned);

        // A second pane merely unhealthy must not downgrade the banner: the
        // abandoned pane's advice is the only advice that helps.
        var change = source.Update(PaneB, RendererHealth.Unhealthy);
        Assert.Null(change.Show);
        Assert.Null(change.Dismiss);
    }

    [Fact]
    public void An_escalation_overrides_a_banner_the_viewer_dismissed()
    {
        var source = new RendererHealthNoticeSource();
        var rebuilding = source.Update(PaneA, RendererHealth.Unhealthy).Show;
        rebuilding!.OnDismiss!();

        // They put away "we are rebuilding". "It is not coming back" is new
        // information and has to reach them anyway.
        Assert.NotNull(source.Update(PaneA, RendererHealth.Abandoned).Show);
    }

    [Fact]
    public void An_abandoned_pane_that_closes_clears_the_banner()
    {
        // Abandoned is terminal, so closing the tab is the only way this pane
        // ever leaves the set. If Forget missed it the banner would outlive
        // every pane it referred to.
        var source = new RendererHealthNoticeSource();
        var raised = source.Update(PaneA, RendererHealth.Abandoned).Show;

        Assert.Same(raised, source.Forget(PaneA).Dismiss);
    }

    [Fact]
    public void Recovering_from_abandoned_is_still_handled()
    {
        // The renderer does not do this today -- abandoned is terminal -- but
        // the set must not strand a pane if that ever changes.
        var source = new RendererHealthNoticeSource();
        var raised = source.Update(PaneA, RendererHealth.Abandoned).Show;

        Assert.Same(raised, source.Update(PaneA, RendererHealth.Healthy).Dismiss);
    }
}
