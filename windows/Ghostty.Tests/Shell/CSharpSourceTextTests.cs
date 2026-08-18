using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// The stripper is the load-bearing part of the wiring tests: every one of
/// them is only as trustworthy as its refusal to see prose and diagnostic
/// strings as code. Both defeats it exists to close are pinned here.
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

    [Fact]
    public void InterpolatedStringContent_IsRemoved()
    {
        var stripped = CSharpSourceText.Strip("Log($\"probe failed {ToastActivations.Note}\");");

        Assert.DoesNotContain("ToastActivations", stripped);
        Assert.Contains("Log(", stripped);
    }

    [Fact]
    public void VerbatimStringContent_IsRemoved()
    {
        var stripped = CSharpSourceText.Strip("var p = @\"C:\\AddArgument\\x\"; Keep();");

        Assert.DoesNotContain("AddArgument", stripped);
        Assert.Contains("Keep();", stripped);
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
