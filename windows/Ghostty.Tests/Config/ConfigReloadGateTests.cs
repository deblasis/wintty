using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// The whole truth table for issue #676's reload rule, both inputs crossed.
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
    [InlineData(true)]
    [InlineData(false)]
    public void A_config_file_that_was_read_always_applies(bool sessionHasConfigFile)
    {
        Assert.Equal(
            ConfigReloadDecision.Apply,
            ConfigReloadGate.Decide(ConfigFilesFound.Loaded, sessionHasConfigFile));
    }

    /// <summary>
    /// A config file that will not open is somebody else holding it. The
    /// config built from it carries defaults where the user's settings
    /// belong, so it never applies, whatever the session was running on.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_config_file_that_would_not_open_never_applies(bool sessionHasConfigFile)
    {
        Assert.Equal(
            ConfigReloadDecision.Decline,
            ConfigReloadGate.Decide(ConfigFilesFound.Unreadable, sessionHasConfigFile));
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
            ConfigReloadGate.Decide(ConfigFilesFound.Absent, sessionHasConfigFile: true));
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
            ConfigReloadGate.Decide(ConfigFilesFound.Absent, sessionHasConfigFile: false));
    }

    /// <summary>
    /// An unknown value from the native side is refused rather than applied.
    /// The enum crosses an FFI boundary, so a value outside it means the two
    /// sides disagree, and the config in hand cannot be trusted.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unknown_answer_is_refused(bool sessionHasConfigFile)
    {
        Assert.Equal(
            ConfigReloadDecision.Decline,
            ConfigReloadGate.Decide((ConfigFilesFound)7, sessionHasConfigFile));
    }

    /// <summary>
    /// Only a load that read a config file leaves the session running on one,
    /// so a session that had one can never lose that flag to a reload: every
    /// other answer is declined before this is consulted.
    /// </summary>
    [Theory]
    [InlineData(ConfigFilesFound.Loaded, true)]
    [InlineData(ConfigFilesFound.Absent, false)]
    [InlineData(ConfigFilesFound.Unreadable, false)]
    public void The_session_runs_on_a_config_file_only_after_one_was_read(
        ConfigFilesFound found, bool expected)
    {
        Assert.Equal(expected, ConfigReloadGate.HasConfigFileAfterApply(found));
    }

    /// <summary>
    /// A locked file is the only decline worth asking about again: its save
    /// has landed and its events are spent. A missing file is mid swap, and
    /// the rename that completes it raises its own.
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
