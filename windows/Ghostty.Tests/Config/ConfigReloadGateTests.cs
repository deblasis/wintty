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
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
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
    /// A locked file is the only decline worth asking about again: its save
    /// has landed and its events are spent. A count that shrank is mid swap,
    /// and the rename that completes it raises its own event; a file that
    /// never comes back is reported as vanished instead.
    /// </summary>
    [Fact]
    public void Only_an_unreadable_config_file_is_retried()
    {
        Assert.True(ConfigReloadGate.ShouldRetry(ConfigFilesFound.Unreadable, 0, 3));
        Assert.False(ConfigReloadGate.ShouldRetry(ConfigFilesFound.Absent, 0, 3));
        Assert.False(ConfigReloadGate.ShouldRetry(ConfigFilesFound.Loaded, 0, 3));
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
