using System;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// The stripper is the load-bearing part of the wiring tests: every one of
/// them is only as trustworthy as its refusal to read prose and diagnostic
/// strings as code. The two defeats it exists to close are pinned here, as are
/// the two holes review found afterwards -- a nested literal leaking out of an
/// interpolation hole, and a raw literal desynchronising the scan.
///
/// Several assertions compare WHOLE output rather than searching it. That is
/// deliberate: for a well-formed literal the quote count is even, so a scanner
/// that mis-handles one still resynchronizes by the end and every substring
/// assertion passes either way. What differs is what it leaves behind.
/// </summary>
public class CSharpSourceTextTests
{
    [Fact]
    public void LineComments_AreRemoved()
    {
        var stripped = CSharpSourceText.Strip("var a = 1; // Register();\nvar b = 2;");

        Assert.DoesNotContain("Register()", stripped);
        Assert.Contains("var b = 2;", stripped);
    }

    [Fact]
    public void WholeLineComments_AreRemoved()
    {
        var stripped = CSharpSourceText.Strip("// NotificationInvoked += Handler;\nCallMe();");

        Assert.DoesNotContain("NotificationInvoked", stripped);
        Assert.Contains("CallMe();", stripped);
    }

    [Fact]
    public void BlockComments_AreRemoved()
    {
        var stripped = CSharpSourceText.Strip("a(); /* Register(); still a comment */ b();");

        Assert.DoesNotContain("Register()", stripped);
        Assert.Contains("a();", stripped);
        Assert.Contains("b();", stripped);
    }

    [Fact]
    public void StringLiteralContent_IsRemoved()
    {
        var stripped = CSharpSourceText.Strip("Log(\"ExtendedActivationKind.AppNotification\");");

        Assert.DoesNotContain("AppNotification", stripped);
        Assert.Contains("Log(", stripped);
    }

    // Whole output, not a substring. The plain-quote path strips the body of
    // $"..." identically, so a "content is removed" assertion passes with the
    // interpolated handling deleted outright. What actually differs is whether
    // the sigil survives into the code text.
    [Fact]
    public void InterpolatedString_IsConsumedIncludingItsSigil()
    {
        var stripped = CSharpSourceText.Strip("Log($\"probe failed {ToastActivations.Note}\");");

        Assert.Equal("Log(\"\");", stripped);
    }

    [Fact]
    public void VerbatimString_IsConsumedIncludingItsSigil()
    {
        var stripped = CSharpSourceText.Strip("var p = @\"C:\\AddArgument\"; Keep();");

        Assert.Equal("var p = \"\"; Keep();", stripped);
    }

    // An interpolation hole is code, so a literal inside one is a literal.
    // Left unparsed, its content lands in the text an assertion reads -- and
    // an indexer keyed by a string is ordinary C#.
    [Fact]
    public void InterpolationHole_NestedStringDoesNotLeak()
    {
        var stripped = CSharpSourceText.Strip("Log($\"cfg={_map[\"theme\"]} done\"); Register();");

        Assert.DoesNotContain("theme", stripped);
        Assert.Equal("Log(\"\"); Register();", stripped);
    }

    // Skipping a nested literal AS a literal only shows when it holds a brace:
    // otherwise ignoring its quotes reaches the same answer. Here the brace
    // inside the nested string closes the hole early, the real closing quote
    // then opens a phantom string, and the rest of the file is eaten.
    [Fact]
    public void InterpolationHole_NestedStringContainingABraceDoesNotEndTheHole()
    {
        var stripped = CSharpSourceText.Strip("Log($\"{Fmt(\"}\")} done\"); Register();");

        Assert.Equal("Log(\"\"); Register();", stripped);
    }

    [Fact]
    public void InterpolationHole_EscapedBracesAreNotHoles()
    {
        var stripped = CSharpSourceText.Strip("Log($\"{{literal}} {x}\"); Keep();");

        Assert.Equal("Log(\"\"); Keep();", stripped);
    }

    // Refused, not guessed at. A raw literal can hold an unbalanced quote
    // count, and a scanner that desynchronises on one deletes real code from
    // the text every wiring assertion reads.
    [Fact]
    public void RawStringLiteral_IsRefusedLoudly()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => CSharpSourceText.Strip("var s = \"\"\"the \" char\"\"\"; Secret();"));

        Assert.Contains("raw string", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterpolatedRawStringLiteral_IsRefusedLoudly()
        => Assert.Throws<NotSupportedException>(
            () => CSharpSourceText.Strip("var s = $$\"\"\"x\"\"\"; Secret();"));

    // A verbatim literal opening with an escaped quote also starts with a run
    // of three, and is not a raw string.
    [Fact]
    public void VerbatimStringOpeningWithAnEscapedQuote_IsNotMistakenForRaw()
    {
        var stripped = CSharpSourceText.Strip("var s = @\"\"\"hi\"\"\"; Keep();");

        Assert.Equal("var s = \"\"; Keep();", stripped);
    }

    [Fact]
    public void EmptyStringLiteral_IsConsumed()
    {
        var stripped = CSharpSourceText.Strip("Log(\"\"); Keep();");

        Assert.Equal("Log(\"\"); Keep();", stripped);
    }

    // A doubled quote inside a verbatim literal is an escaped quote, not the
    // end of it.
    //
    // Asserted on the whole output, not by substring. A scanner that stops at
    // the first quote of the pair treats the rest as an alternating run of
    // strings, and the quote count of a well-formed literal is always even --
    // so it resynchronizes at the end and every substring assertion passes
    // either way. What differs is how many empty literals it leaves behind.
    [Fact]
    public void VerbatimString_DoubledQuoteDoesNotEndIt()
    {
        var stripped = CSharpSourceText.Strip("var s = @\"p\"\"AddArgument\"\"q\"; Keep();");

        Assert.Equal("var s = \"\"; Keep();", stripped);
    }

    [Fact]
    public void RegularString_EscapedQuoteDoesNotEndIt()
    {
        var stripped = CSharpSourceText.Strip("var s = \"a\\\"Register();b\"; Keep();");

        Assert.DoesNotContain("Register()", stripped);
        Assert.Contains("Keep();", stripped);
    }

    // A char literal holding a quote used to open a phantom string and swallow
    // everything after it.
    [Fact]
    public void CharLiteralHoldingAQuote_DoesNotOpenAString()
    {
        var stripped = CSharpSourceText.Strip("if (c == '\"') Register();");

        Assert.Contains("Register();", stripped);
    }

    [Fact]
    public void CharLiteralHoldingAnEscapedQuote_DoesNotOpenAString()
    {
        var stripped = CSharpSourceText.Strip("if (c == '\\'') Register();");

        Assert.Contains("Register();", stripped);
    }

    [Fact]
    public void Member_ScopesToTheDeclaredBody()
    {
        const string source = """
            class C
            {
                void First() { Alpha(); }
                void Second() { Beta(); }
            }
            """;

        var first = CSharpSourceText.Member(source, "void First()");

        Assert.Contains("Alpha();", first);
        Assert.DoesNotContain("Beta();", first);
    }

    [Fact]
    public void Member_StripsInsideTheBody()
    {
        const string source = """
            class C
            {
                void First()
                {
                    // Beta();
                    Alpha();
                }
            }
            """;

        var first = CSharpSourceText.Member(source, "void First()");

        Assert.Contains("Alpha();", first);
        Assert.DoesNotContain("Beta();", first);
    }
}
