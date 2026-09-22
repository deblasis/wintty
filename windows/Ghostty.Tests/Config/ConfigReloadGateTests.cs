using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// The whole truth table for issue #676's reload rule, every input crossed.
///
/// These are the assertions the wiring tests cannot make. A wiring test reads
/// the call site and can only say the guard is shaped the way it was left;
/// flipping <c>||</c> to <c>&amp;&amp;</c>, or dropping a <c>!</c>, keeps every
/// substring it looks for and changes what the app does.
/// </summary>
public class ConfigReloadGateTests
{
    /// <summary>
    /// A config file that was read is the user's, so it applies. That covers
    /// an empty one: emptying the file is a configuration that asks for
    /// nothing, and the defaults coming back is what it says.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_config_file_that_was_read_always_applies(int sessionFilesFound)
    {
        Assert.Equal(
            ConfigReloadDecision.Apply,
            ConfigReloadGate.Decide(ConfigFilesFound.Loaded, 1, sessionFilesFound));
    }

    /// <summary>
    /// A config file that will not open is somebody else holding it. The
    /// config built from it carries defaults where the user's settings
    /// belong, so it never applies, whatever the session was running on.
    ///
    /// Every row has at least one file, because Unreadable says one is
    /// there. A count of zero beside it is the two sides disagreeing, which
    /// is its own case below.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void A_config_file_that_would_not_open_never_applies(
        int filesFound, int sessionFilesFound)
    {
        Assert.Equal(
            ConfigReloadDecision.Decline,
            ConfigReloadGate.Decide(ConfigFilesFound.Unreadable, filesFound, sessionFilesFound));
    }

    /// <summary>
    /// The one that the two obvious mutations of the guard get wrong. A
    /// session with a config file that suddenly has none is mid atomic save,
    /// and applying pure defaults resets a terminal the user is looking at.
    /// </summary>
    [Fact]
    public void No_config_file_is_refused_for_a_session_that_has_one()
    {
        Assert.Equal(
            ConfigReloadDecision.Decline,
            ConfigReloadGate.Decide(ConfigFilesFound.Absent, 0, 1));
    }

    /// <summary>
    /// And the other direction: a machine with no config file at all still
    /// reloads. That is the path a High Contrast change takes, and the
    /// override only reaches the terminal through the config a reload builds.
    /// </summary>
    [Fact]
    public void No_config_file_still_applies_for_a_session_that_never_had_one()
    {
        Assert.Equal(
            ConfigReloadDecision.Apply,
            ConfigReloadGate.Decide(ConfigFilesFound.Absent, 0, 0));
    }

    /// <summary>
    /// The case the verdict on its own cannot see, and the reason the count
    /// is there. There are three default config files and they layer: a user
    /// migrated from Ghostty has ghostty/config.ghostty beside
    /// wintty/config.wintty, which this fork still reads. While an editor
    /// swaps the newer one in, the older one still reads, so the load
    /// reports a perfectly good <c>Loaded</c> with one file fewer, and every
    /// setting from the file being saved is missing from it.
    /// </summary>
    [Fact]
    public void A_config_file_the_session_had_going_missing_is_refused()
    {
        Assert.Equal(
            ConfigReloadDecision.Decline,
            ConfigReloadGate.Decide(ConfigFilesFound.Loaded, 1, 2));
    }

    /// <summary>
    /// And a config file appearing is not a reason to refuse: writing the
    /// second one is an ordinary edit, and the config built from it is the
    /// user's.
    /// </summary>
    [Fact]
    public void A_config_file_appearing_applies()
    {
        Assert.Equal(
            ConfigReloadDecision.Apply,
            ConfigReloadGate.Decide(ConfigFilesFound.Loaded, 2, 1));
    }

    /// <summary>
    /// The verdict and the count say the same thing twice, and the gate
    /// checks that rather than assuming it.
    /// </summary>
    /// <remarks>
    /// They cross the FFI as two values, and the dangerous direction is the
    /// second row: an Absent claiming a non-zero count walks straight
    /// through the shrink comparison, and what it lets through is a config
    /// of pure defaults applied at every live surface. The first row is the
    /// same disagreement the other way. Both are impossible from a correct
    /// loader, which is the point: the day one is possible, this refuses it
    /// instead of applying it.
    /// </remarks>
    [Theory]
    [InlineData(ConfigFilesFound.Loaded, 0)]
    [InlineData(ConfigFilesFound.Absent, 1)]
    [InlineData(ConfigFilesFound.Absent, 2)]
    public void A_verdict_that_disagrees_with_the_count_is_refused(
        ConfigFilesFound found, int filesFound)
    {
        // Session counts chosen so the shrink comparison on its own would
        // say Apply: without the agreement check these all reload.
        Assert.Equal(
            ConfigReloadDecision.Decline,
            ConfigReloadGate.Decide(found, filesFound, 0));
    }

    /// <summary>
    /// An unknown value from the native side is refused rather than applied.
    /// The enum crosses an FFI boundary, so a value outside it means the two
    /// sides disagree, and the config in hand cannot be trusted.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void An_unknown_answer_is_refused(int sessionFilesFound)
    {
        Assert.Equal(
            ConfigReloadDecision.Decline,
            ConfigReloadGate.Decide((ConfigFilesFound)7, 1, sessionFilesFound));
    }

    /// <summary>
    /// A locked file is the only decline worth asking about again on its
    /// own: its save has landed and its events are spent. A count that
    /// shrank takes the separate confirm ask below, and a watched file
    /// that never comes back is reported as vanished instead.
    /// </summary>
    [Fact]
    public void Only_an_unreadable_config_file_is_retried()
    {
        Assert.True(ConfigReloadGate.ShouldRetry(ConfigFilesFound.Unreadable, 0, 3));
        Assert.False(ConfigReloadGate.ShouldRetry(ConfigFilesFound.Absent, 0, 3));
        Assert.False(ConfigReloadGate.ShouldRetry(ConfigFilesFound.Loaded, 0, 3));
    }

    /// <summary>
    /// A shrunk count is either a save mid swap or a file gone for good,
    /// and the confirm ask is how the two are told apart: the save's
    /// completing rename answers it, so a shrink worth asking about stops
    /// being asked about the moment the budget is spent. Only a Loaded
    /// count takes it; an Unreadable one asks through ShouldRetry, and an
    /// Absent one is the vanished callback's case, decided on a whole
    /// quiet period of the watched file being gone.
    /// </summary>
    [Theory]
    [InlineData(ConfigFilesFound.Loaded, 1, 2, 0, 3, true)]
    [InlineData(ConfigFilesFound.Loaded, 1, 2, 2, 3, true)]
    [InlineData(ConfigFilesFound.Loaded, 1, 2, 3, 3, false)]
    [InlineData(ConfigFilesFound.Loaded, 2, 1, 0, 3, false)]
    [InlineData(ConfigFilesFound.Absent, 0, 1, 0, 3, false)]
    [InlineData(ConfigFilesFound.Unreadable, 1, 2, 0, 3, false)]
    public void A_shrunk_count_gets_one_more_look_while_the_budget_lasts(
        ConfigFilesFound found,
        int filesFound,
        int sessionFilesFound,
        int attemptsSoFar,
        int maxAttempts,
        bool expected)
    {
        Assert.Equal(
            expected,
            ConfigReloadGate.ShouldConfirmShrink(
                found, filesFound, sessionFilesFound, attemptsSoFar, maxAttempts));
    }

    /// <summary>
    /// A shrink that is still a shrink after the whole budget was spent on
    /// looking again is a deletion, not a save: every ask waited out a full
    /// quiet period, and a rename that slow lost its race with its own
    /// editor. The deleted file is one the watcher does not watch - the
    /// layered candidates it never sees - so nothing else would ever lower
    /// the session count, and refusing here is the permanent lockout of
    /// issue #676 again.
    /// </summary>
    [Theory]
    [InlineData(ConfigFilesFound.Loaded, 1, 2, 3, 3, true)]
    [InlineData(ConfigFilesFound.Loaded, 1, 2, 4, 3, true)]
    [InlineData(ConfigFilesFound.Loaded, 1, 2, 2, 3, false)]
    [InlineData(ConfigFilesFound.Loaded, 2, 1, 3, 3, false)]
    [InlineData(ConfigFilesFound.Absent, 0, 1, 3, 3, false)]
    public void A_shrink_that_outlives_the_whole_budget_is_a_deletion(
        ConfigFilesFound found,
        int filesFound,
        int sessionFilesFound,
        int attemptsSoFar,
        int maxAttempts,
        bool expected)
    {
        Assert.Equal(
            expected,
            ConfigReloadGate.IsPersistentShrink(
                found, filesFound, sessionFilesFound, attemptsSoFar, maxAttempts));
    }

    /// <summary>
    /// And the retry is bounded, so a file that stays locked settles into
    /// keeping what is running rather than rebuilding the config every
    /// debounce period for the life of the process.
    /// </summary>
    [Fact]
    public void The_retry_budget_runs_out()
    {
        Assert.True(ConfigReloadGate.ShouldRetry(ConfigFilesFound.Unreadable, 2, 3));
        Assert.False(ConfigReloadGate.ShouldRetry(ConfigFilesFound.Unreadable, 3, 3));
        Assert.False(ConfigReloadGate.ShouldRetry(ConfigFilesFound.Unreadable, 4, 3));
    }

    /// <summary>
    /// A vanished watched file is asked about until the budget is spent,
    /// because one observation of it is what an ordinary atomic save
    /// produces.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void A_vanished_config_file_is_asked_about_until_the_budget_is_spent(
        int attemptsSoFar, bool expected)
    {
        Assert.Equal(
            expected,
            ConfigReloadGate.ShouldConfirmVanish(1, attemptsSoFar, 3));
    }

    /// <summary>
    /// And only once the budget is spent is it a deletion. The two are
    /// complements over a session that has files, so there is no attempt
    /// count at which the host neither asks nor concludes: such a gap would
    /// leave it refusing reloads with nothing left that could resolve them.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Asking_and_concluding_cover_every_attempt_count(int attemptsSoFar)
    {
        var asks = ConfigReloadGate.ShouldConfirmVanish(1, attemptsSoFar, 3);
        var concludes = ConfigReloadGate.IsPersistentVanish(1, attemptsSoFar, 3);

        Assert.NotEqual(asks, concludes);
    }

    /// <summary>
    /// A session claiming no config file has nothing to confirm and nothing
    /// to lower, so a vanish neither asks nor concludes.
    /// </summary>
    [Fact]
    public void A_session_running_on_no_config_file_does_neither()
    {
        Assert.False(ConfigReloadGate.ShouldConfirmVanish(0, 0, 3));
        Assert.False(ConfigReloadGate.IsPersistentVanish(0, 3, 3));
    }

    /// <summary>
    /// The managed enum is the C one. A renumber here and a load that read
    /// the user's config arrives as one that found nothing.
    /// </summary>
    [Fact]
    public void The_values_are_the_ones_the_C_header_publishes()
    {
        Assert.Equal(-1, (int)ConfigFilesFound.Unreadable);
        Assert.Equal(0, (int)ConfigFilesFound.Absent);
        Assert.Equal(1, (int)ConfigFilesFound.Loaded);
    }
}
