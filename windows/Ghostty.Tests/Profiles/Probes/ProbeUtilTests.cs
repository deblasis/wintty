using Ghostty.Core.Profiles.Probes;
using Xunit;

namespace Ghostty.Tests.Profiles.Probes;

public sealed class ProbeUtilTests
{
    [Fact]
    public void QuoteIfNeeded_PathWithoutSpace_ReturnsUnchanged()
    {
        var result = ProbeUtil.QuoteIfNeeded(@"C:\Windows\System32\cmd.exe");
        Assert.Equal(@"C:\Windows\System32\cmd.exe", result);
    }

    [Fact]
    public void QuoteIfNeeded_PathWithSpace_ReturnsQuoted()
    {
        var result = ProbeUtil.QuoteIfNeeded(@"C:\Program Files\Git\bin\bash.exe");
        Assert.Equal(@"""C:\Program Files\Git\bin\bash.exe""", result);
    }

    [Fact]
    public void QuoteIfNeeded_AlreadyQuoted_ReturnsUnchanged()
    {
        var input = @"""C:\Program Files\Git\bin\bash.exe""";
        var result = ProbeUtil.QuoteIfNeeded(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void QuoteIfNeeded_EmptyString_ReturnsEmpty()
    {
        var result = ProbeUtil.QuoteIfNeeded(string.Empty);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void QuoteIfNeeded_EmbeddedQuote_EscapesAndWraps()
    {
        // WSL distro names are technically allowed to contain " characters.
        // The helper must escape them so CreateProcessW does not split the
        // argv token at the inner quote. Without escaping, a distro named
        // Foo"Bar would produce: wsl.exe -d "Foo"Bar"  -> two argv tokens.
        var result = ProbeUtil.QuoteIfNeeded("Foo\"Bar");
        Assert.Equal("\"Foo\\\"Bar\"", result);
    }

    [Fact]
    public void QuoteIfNeeded_QuoteAndSpace_EscapesAndWraps()
    {
        var result = ProbeUtil.QuoteIfNeeded("Foo \"Bar\" Baz");
        Assert.Equal("\"Foo \\\"Bar\\\" Baz\"", result);
    }

    [Fact]
    public void QuoteIfNeeded_LeadingQuoteOnly_TreatedAsValueToWrap()
    {
        // A single leading " (not balanced by a trailing ") is NOT a
        // pre-wrapped value and must be escaped + wrapped.
        var result = ProbeUtil.QuoteIfNeeded("\"unbalanced");
        Assert.Equal("\"\\\"unbalanced\"", result);
    }
}
