using Ghostty.Core.Shell;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// <see cref="BackdropStyles.TryNormalize"/> is the only place a raw
/// config value becomes a style. Four separate literals used to stand in
/// for the default, and the comparisons downstream are ordinal, so a
/// config saying "Frosted" ran solid without a word about it.
/// </summary>
public sealed class BackdropStylesTests
{
    [Theory]
    [InlineData(BackdropStyles.Frosted)]
    [InlineData(BackdropStyles.Crystal)]
    [InlineData(BackdropStyles.Solid)]
    public void Canonical_values_round_trip(string canonical)
    {
        Assert.True(BackdropStyles.TryNormalize(canonical, out var style));
        Assert.Equal(canonical, style);
    }

    [Theory]
    [InlineData("Frosted", BackdropStyles.Frosted)]
    [InlineData("FROSTED", BackdropStyles.Frosted)]
    [InlineData("  frosted  ", BackdropStyles.Frosted)]
    [InlineData("\tSolid\r\n", BackdropStyles.Solid)]
    [InlineData("Crystal", BackdropStyles.Crystal)]
    public void Case_and_surrounding_space_fold_to_the_canonical_value(string raw, string expected)
    {
        Assert.True(BackdropStyles.TryNormalize(raw, out var style));
        Assert.Equal(expected, style);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("glass")]
    [InlineData("mica")]
    public void Unset_or_unrecognised_reports_false_and_yields_the_default(string? raw)
    {
        Assert.False(BackdropStyles.TryNormalize(raw, out var style));
        Assert.Equal(BackdropStyles.Default, style);
    }

    /// <summary>
    /// The fallback has to be a value the rest of the shell can act on.
    /// A default that does not normalise would make every unrecognised
    /// read report false twice over.
    /// </summary>
    [Fact]
    public void The_default_is_itself_a_canonical_style()
    {
        Assert.True(BackdropStyles.TryNormalize(BackdropStyles.Default, out var style));
        Assert.Equal(BackdropStyles.Default, style);
    }
}
