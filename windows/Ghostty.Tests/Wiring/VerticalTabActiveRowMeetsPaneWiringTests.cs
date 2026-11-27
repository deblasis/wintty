using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// <c>ActiveRowMeetsThePane</c> answers the seam cover by looking the active tab
/// up in two dictionaries, and a dictionary throws on a null key. The property
/// is declared non-null, but <c>TabManager</c> backs the value with <c>null!</c>,
/// so the annotation is a promise rather than a fact, and the read has to settle
/// the null before it reaches the lookup. <c>UpdateSelectionRow</c> already does,
/// on the same tab, one line above where it asks for the row; this pins the
/// property to the same discipline.
///
/// The pin is the body's tokens. Judging the expression instead would mean
/// deciding, from syntax alone, whether the lookup is reached with the tab
/// established, and that judgement has two ways to be wrong: it can accept a
/// form that still throws (a check appended after the lookup, a lookup inside a
/// switch arm, an argument list or an interpolation) and it can reject a safe
/// form written differently. Comparing tokens cannot accept a crashing form at
/// all. The cost is that any change to the body's text fails here, including a
/// reformatting, until the expected body below is updated. That is taken
/// deliberately over comparing tokens: reading the tokens back means parsing
/// the body a second time, and a second parse can see a different expression
/// than the one the file was first read with.
/// </summary>
public sealed class VerticalTabActiveRowMeetsPaneWiringTests
{
    private const string ExpectedBody =
        "_manager.ActiveTab is { } tab && RowElementOf(tab) is not VerticalTabPinnedRow";

    [Fact]
    public void ActiveRowMeetsThePane_SettlesTheMissingTabBeforeTheLookup()
    {
        var property = Assert.Single(
            ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs").Root
                .DescendantNodes()
                .OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.ValueText == "ActiveRowMeetsThePane");

        Assert.True(
            property.ExpressionBody is not null,
            "`ActiveRowMeetsThePane` must stay an expression-bodied property: its whole answer is "
            + "pinned as one expression, and a block body needs its own review");

        Assert.Equal(ExpectedBody, property.ExpressionBody!.Expression.ToString());
    }
}
