using Ghostty.Core.Motion;
using Xunit;

namespace Ghostty.Tests.Motion;

/// <summary>
/// The <c>animations</c> key's vocabulary: the three written values, the
/// enum rung each maps to, and the fallback. The settings combo's tags,
/// the persistence round-trip and the gate's lever seat all read these
/// spellings, so the table is pinned here rather than wherever a caller
/// happens to spell it.
/// </summary>
public class UserMotionLeverValuesTests
{
    [Theory]
    [InlineData("system", UserMotionLever.FollowSystem)]
    [InlineData("reduced", UserMotionLever.Reduced)]
    [InlineData("off", UserMotionLever.Off)]
    [InlineData("SYSTEM", UserMotionLever.FollowSystem)]   // case-folded
    [InlineData(" system ", UserMotionLever.FollowSystem)] // trimmed
    [InlineData("follow", UserMotionLever.FollowSystem)]   // unrecognized falls back
    [InlineData("", UserMotionLever.FollowSystem)]
    public void Parse_maps_the_written_value_to_the_rung(string value, UserMotionLever expected)
        => Assert.Equal(expected, UserMotionLeverValues.Parse(value));

    [Fact]
    public void Parse_null_defaults_to_follow_system()
        => Assert.Equal(UserMotionLever.FollowSystem, UserMotionLeverValues.Parse(null));

    [Theory]
    [InlineData(UserMotionLever.FollowSystem, "system")]
    [InlineData(UserMotionLever.Reduced, "reduced")]
    [InlineData(UserMotionLever.Off, "off")]
    public void ConfigValue_spells_the_written_form(UserMotionLever lever, string expected)
        => Assert.Equal(expected, UserMotionLeverValues.ConfigValue(lever));

    [Fact]
    public void Round_trip_through_the_written_form_is_identity()
    {
        foreach (var lever in new[]
                 {
                     UserMotionLever.FollowSystem,
                     UserMotionLever.Reduced,
                     UserMotionLever.Off,
                 })
        {
            Assert.Equal(lever, UserMotionLeverValues.Parse(UserMotionLeverValues.ConfigValue(lever)));
        }
    }
}
