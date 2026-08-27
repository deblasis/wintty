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
    /// A translucent backdrop is the only thing that makes a translucent
    /// frame mean anything, so it is the only case where the frame's own
    /// material survives the fold. Both translucent backdrops are covered:
    /// they are indistinguishable as frames and were briefly assumed to be
    /// indistinguishable as grounds too.
    ///
    /// The rows where the two styles differ are the load-bearing ones. Read
    /// the arguments the wrong way round and a crystal frame over a frosted
    /// backdrop comes back frosted, which every matching row would accept.
    /// </summary>
    [Theory]
    [InlineData(BackdropStyles.Frosted, BackdropStyles.Frosted)]
    [InlineData(BackdropStyles.Crystal, BackdropStyles.Frosted)]
    [InlineData(BackdropStyles.Solid, BackdropStyles.Frosted)]
    [InlineData(BackdropStyles.Frosted, BackdropStyles.Crystal)]
    [InlineData(BackdropStyles.Crystal, BackdropStyles.Crystal)]
    [InlineData(BackdropStyles.Solid, BackdropStyles.Crystal)]
    public void A_translucent_backdrop_leaves_the_frames_material_alone(
        string frameStyle, string backdropStyle)
    {
        Assert.Equal(frameStyle, BackdropStyles.FrameOver(frameStyle, backdropStyle));
    }

    /// <summary>
    /// And a solid one takes it away, whichever of the two translucent
    /// values was asked for. The window has one SystemBackdrop, so there is
    /// nothing on the far side of the frame to reveal, and resolving it
    /// transparent exposes the window's own opaque root instead -- which
    /// under window-theme=wintty is the terminal's own colour.
    ///
    /// The unrecognised rows are the pre-init field and anything a future
    /// style adds: everything that is not a translucent backdrop is a ground
    /// of its own, which is the same rule the colour resolver applies.
    /// </summary>
    [Theory]
    [InlineData(BackdropStyles.Frosted, BackdropStyles.Solid)]
    [InlineData(BackdropStyles.Crystal, BackdropStyles.Solid)]
    [InlineData(BackdropStyles.Solid, BackdropStyles.Solid)]
    [InlineData(BackdropStyles.Frosted, "")]
    [InlineData(BackdropStyles.Crystal, "mica")]
    public void An_opaque_backdrop_degrades_the_frame_to_solid(
        string frameStyle, string backdropStyle)
    {
        Assert.Equal(BackdropStyles.Solid, BackdropStyles.FrameOver(frameStyle, backdropStyle));
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
