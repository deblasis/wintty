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
        // A surface the renderer has given up on stays dark for the life of
        // the tab. Copy saying the rebuild finishes would leave that user
        // waiting on something that is never going to happen.
        var notice = new RendererHealthNoticeSource().Update(PaneA, RendererHealth.Unhealthy).Show;

        Assert.NotNull(notice);
        foreach (var phrase in new[] { "until it finishes", "will be back", "shortly" })
        {
            Assert.DoesNotContain(phrase, notice!.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        // And says so positively, rather than only avoiding the promise.
        Assert.Contains("may not come back", notice!.Message, System.StringComparison.OrdinalIgnoreCase);
    }
}
