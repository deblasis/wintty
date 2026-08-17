using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

public class WindowsOnlyKeyParsersTests
{
    [Theory]
    [InlineData("true", true, true)]
    [InlineData("TRUE", true, true)]
    [InlineData("false", true, false)]
    [InlineData("FALSE", true, false)]
    [InlineData("1", true, true)]        // non-canonical -> default (true)
    [InlineData("1", false, false)]      // non-canonical -> default (false)
    [InlineData("0", true, true)]        // non-canonical -> default
    [InlineData("yes", false, false)]   // unknown token -> default
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    [InlineData("  true  ", false, true)]   // surrounding whitespace tolerated
    [InlineData("\tfalse\n", true, false)]
    public void ParseBool_accepts_canonical_forms(string? raw, bool defaultValue, bool expected)
    {
        Assert.Equal(expected, WindowsOnlyKeyParsers.ParseBool(raw, defaultValue));
    }

    [Fact]
    public void ParseBool_returns_default_when_unknown()
    {
        Assert.True(WindowsOnlyKeyParsers.ParseBool("maybe", defaultValue: true));
        Assert.False(WindowsOnlyKeyParsers.ParseBool("maybe", defaultValue: false));
    }

    [Theory]
    [InlineData("acrylic", "acrylic")]
    [InlineData("MICA", "mica")]
    [InlineData("opaque", "opaque")]
    [InlineData("", "acrylic")]
    [InlineData(null, "acrylic")]
    [InlineData("bogus", "acrylic")]
    public void ParseStringAllowed_normalizes_and_falls_back(string? raw, string expected)
    {
        var result = WindowsOnlyKeyParsers.ParseStringAllowed(
            raw,
            allowed: new[] { "acrylic", "mica", "opaque" },
            defaultValue: "acrylic");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, 220, 80, 600, 220)]
    [InlineData("", 220, 80, 600, 220)]
    [InlineData("300", 220, 80, 600, 300)]
    [InlineData(" 180 ", 220, 80, 600, 180)]
    [InlineData("10", 220, 80, 600, 80)]
    [InlineData("9999", 220, 80, 600, 600)]
    [InlineData("nope", 220, 80, 600, 220)]
    [InlineData("220px", 220, 80, 600, 220)]
    public void ParseIntClamped_Normalizes(
        string? raw, int fallback, int min, int max, int expected)
    {
        Assert.Equal(expected, WindowsOnlyKeyParsers.ParseIntClamped(raw, fallback, min, max));
    }
}
