using System;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// The vanish confirmation, driven rather than read.
/// </summary>
/// <remarks>
/// These exist because the source-shape tests over <c>ConfigService</c>
/// could not fail. Nothing executes that file, so a budget of zero and an
/// increment moved out from behind the scheduled-ask check both passed the
/// whole suite while restoring the #1146 defect exactly. Every assertion
/// here runs the thing.
/// </remarks>
public class ConfigVanishConfirmerTests
{
    private static Func<bool> Armed => () => true;
    private static Func<bool> Dropped => () => false;

    /// <summary>
    /// The one the shape tests could not make. A budget of zero turns the
    /// asks off and accepts the first report, which is the defect, so it is
    /// not a configuration this can be put into.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_budget_that_would_believe_the_first_report_is_refused(int maxAttempts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConfigVanishConfirmer(maxAttempts));
    }

    /// <summary>
    /// And the default is a real budget, so the shipped configuration asks
    /// before it believes. This is the assertion whose absence let the
    /// constant be zeroed unnoticed.
    /// </summary>
    [Fact]
    public void The_default_budget_asks_before_believing()
    {
        Assert.True(ConfigVanishConfirmer.DefaultAttempts >= 1);

        var confirmer = new ConfigVanishConfirmer();
        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Armed));
    }

    /// <summary>
    /// A whole stretch: every ask inside the budget confirms, and only the
    /// report past it is a deletion.
    /// </summary>
    /// <remarks>
    /// The report after the Accept confirms afresh rather than staying
    /// accepted: that trailing assertion once held, and the state it
    /// pinned, a budget spent past its own conclusion, is exactly what
    /// let the next save's first mid-swap report be accepted at once once
    /// the host's count came back. The real host zeroes its count on the
    /// accept, so a positive count at the next report always means the
    /// file was seen again, and the question is new.
    /// </remarks>
    [Fact]
    public void It_asks_for_the_whole_budget_and_then_accepts()
    {
        var confirmer = new ConfigVanishConfirmer(3);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Armed));
        }

        Assert.Equal(3, confirmer.Attempts);
        Assert.Equal(ConfigVanishAction.Accept, confirmer.Observe(1, Armed));
        Assert.Equal(0, confirmer.Attempts);
    }

    /// <summary>
    /// An ask the watcher dropped was never put, so it does not spend the
    /// budget. Counting it confirms a deletion out of silence.
    /// </summary>
    [Fact]
    public void A_dropped_ask_does_not_spend_the_budget()
    {
        var confirmer = new ConfigVanishConfirmer(3);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Dropped));
        }

        Assert.Equal(0, confirmer.Attempts);
    }

    /// <summary>
    /// A concluded stretch leaves no budget behind. The host pairs Accept
    /// with lowering its count, and the file can come back through paths
    /// that never settle a delivery: the app's own writes suppress the
    /// watcher, so its events arm nothing and the return is seen only by
    /// the reload that follows. A budget still spent there accepted the
    /// next ordinary save's first mid-swap report at once, which is #1146
    /// re-entered through the stale budget. The conclusion, not the
    /// report, is what the budget cannot outlive.
    /// </summary>
    [Fact]
    public void A_concluded_stretch_leaves_no_budget_behind()
    {
        var confirmer = new ConfigVanishConfirmer(3);
        for (var i = 0; i < 3; i++) confirmer.Observe(1, Armed);
        Assert.Equal(ConfigVanishAction.Accept, confirmer.Observe(1, Armed));

        // The count is back: the host's own record of a load that found
        // the file, the state a suppressed-write return leaves behind.
        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Armed));
        Assert.Equal(1, confirmer.Attempts);
    }

    /// <summary>
    /// And a session the conclusion left nothing to lose ignores what
    /// arrives next, which is the state the host is in right after an
    /// Accept: reports about a file it no longer claims cost nothing and
    /// ask nothing.
    /// </summary>
    [Fact]
    public void A_session_with_nothing_left_ignores_reports_after_an_accept()
    {
        var confirmer = new ConfigVanishConfirmer(3);
        for (var i = 0; i < 3; i++) confirmer.Observe(1, Armed);
        Assert.Equal(ConfigVanishAction.Accept, confirmer.Observe(1, Armed));

        Assert.Equal(ConfigVanishAction.Ignore, confirmer.Observe(0, Armed));
        Assert.Equal(0, confirmer.Attempts);
    }

    /// <summary>
    /// The file being there answers the question, so the next vanish starts
    /// from a full budget. Without this a spent stretch outlives the thing
    /// that resolved it and the next ordinary save is believed at once.
    /// </summary>
    [Fact]
    public void Evidence_that_answers_the_question_restores_the_budget()
    {
        var confirmer = new ConfigVanishConfirmer(3);
        for (var i = 0; i < 3; i++) confirmer.Observe(1, Armed);
        Assert.Equal(ConfigVanishAction.Accept, confirmer.Observe(1, Armed));

        confirmer.Reset();

        Assert.Equal(0, confirmer.Attempts);
        Assert.Equal(ConfigVanishAction.Confirm, confirmer.Observe(1, Armed));
    }

    /// <summary>
    /// A session claiming no config file has nothing to lose and nothing to
    /// confirm, so a report neither asks nor concludes, however many arrive.
    /// </summary>
    [Fact]
    public void A_session_running_on_no_config_file_ignores_the_report()
    {
        var confirmer = new ConfigVanishConfirmer(3);

        Assert.Equal(ConfigVanishAction.Ignore, confirmer.Observe(0, Armed));
        Assert.Equal(ConfigVanishAction.Ignore, confirmer.Observe(0, Armed));
        Assert.Equal(0, confirmer.Attempts);
    }

    /// <summary>
    /// The ask is put once per report and only while the budget lasts, so a
    /// deletion costs a bounded number of deliveries rather than one per
    /// report for the life of the process.
    /// </summary>
    /// <remarks>
    /// The bound is per stretch, and a stretch ends twice over: at the
    /// accept, whose conclusion restores the budget, and at the host's own
    /// answer to an accept, which lowers its count so the reports after the
    /// conclusion are Ignored. The pair is what bounds the asks. A test
    /// holding the count at one forever, as this one once did, models a
    /// host that ignores its own accept, and under the conclusion-side
    /// restore that host asks three more on every fourth report without
    /// end: the accept fires, the count stays, the question restarts.
    /// </remarks>
    [Fact]
    public void The_asks_stop_when_the_budget_does()
    {
        var asks = 0;
        bool Ask() { asks++; return true; }
        var confirmer = new ConfigVanishConfirmer(3);

        // The host's count, with the host's half of the contract: an
        // accept is the session dropping it to zero.
        var count = 1;

        ConfigVanishAction action;
        do
        {
            action = confirmer.Observe(count, Ask);
            if (action == ConfigVanishAction.Accept) count = 0;
        }
        while (action != ConfigVanishAction.Accept);

        Assert.Equal(3, asks);

        // And the reports that keep arriving after the conclusion cost
        // nothing, because the session has nothing left to ask about.
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(ConfigVanishAction.Ignore, confirmer.Observe(count, Ask));
        }

        Assert.Equal(3, asks);
    }
}
