using System;
using System.Collections.Generic;
using Ghostty.Core.Commands;
using Xunit;

namespace Ghostty.Tests.Commands;

/// <summary>
/// The palette's selection rules, which used to live inline in the view
/// model and so could not be exercised at all: the view model is in the
/// WinUI project, which this assembly cannot reference.
///
/// The bug behind <see cref="PaletteSelection.SelectTop"/> was real. A query
/// matching nothing left the previously selected command selected, and
/// ExecuteSelectedCommand guards only against null, so Enter ran a command
/// that was not on screen.
/// </summary>
public class PaletteSelectionTests
{
    private sealed record Row(string Name);

    private static readonly List<Row> Three =
        [new Row("a"), new Row("b"), new Row("c")];

    [Fact]
    public void Step_FromNothing_SelectsTheFirstItemInEitherDirection()
    {
        // Up and Down disagree about which end is "next" but agree that a
        // list with no selection should end up selecting something.
        Assert.Equal(Three[0], PaletteSelection.Step(Three, null, -1));
        Assert.Equal(Three[0], PaletteSelection.Step(Three, null, +1));
    }

    [Theory]
    [InlineData(0, +1, 1)]
    [InlineData(1, +1, 2)]
    [InlineData(2, -1, 1)]
    [InlineData(1, -1, 0)]
    public void Step_MovesOne(int from, int delta, int expected)
    {
        Assert.Equal(Three[expected], PaletteSelection.Step(Three, Three[from], delta));
    }

    [Theory]
    [InlineData(2, +1, 2)]
    [InlineData(0, -1, 0)]
    public void Step_ClampsAtTheEnds(int from, int delta, int expected)
    {
        // Not a wrap. Arrowing past the end of a palette list stays put
        // rather than jumping to the far end under the user's fingers.
        Assert.Equal(Three[expected], PaletteSelection.Step(Three, Three[from], delta));
    }

    [Theory]
    [InlineData(int.MaxValue, 2)]
    [InlineData(int.MinValue, 0)]
    public void Step_ClampsWithoutOverflowing(int delta, int expected)
    {
        // index + delta in int arithmetic wraps, and a wrapped negative
        // clamps to the FIRST item, the opposite end from the one asked for.
        // Only reachable from a paging binding, which is exactly the caller
        // that would pass a large delta.
        Assert.Equal(Three[expected], PaletteSelection.Step(Three, Three[1], delta));
    }

    [Fact]
    public void Step_OnAnEmptyList_SelectsNothing()
    {
        // An empty list has to end with nothing selected, not with the
        // previous selection surviving into it.
        Assert.Null(PaletteSelection.Step(new List<Row>(), Three[1], +1));
        Assert.Null(PaletteSelection.Step(new List<Row>(), null, -1));
    }

    [Fact]
    public void Step_FromAnItemThatIsGone_SelectsTheFirst()
    {
        Assert.Equal(Three[0], PaletteSelection.Step(Three, new Row("gone"), +1));
    }

    [Fact]
    public void SelectTop_TakesTheFirstOrNothing()
    {
        // The point is that it has no non-assigning branch.
        Assert.Equal(Three[0], PaletteSelection.SelectTop(Three));
        Assert.Null(PaletteSelection.SelectTop(new List<Row>()));
    }

    [Fact]
    public void BothRejectANullList()
    {
        Assert.Throws<ArgumentNullException>(
            () => PaletteSelection.Step<Row>(null!, null, +1));
        Assert.Throws<ArgumentNullException>(
            () => PaletteSelection.SelectTop<Row>(null!));
    }
}
