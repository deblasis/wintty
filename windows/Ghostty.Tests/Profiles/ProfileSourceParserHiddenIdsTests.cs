using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class ProfileSourceParserHiddenIdsTests
{
    [Fact]
    public void ExtractHiddenIds_EmptyText_ReturnsEmpty()
    {
        var result = ProfileSourceParser.ExtractHiddenIds(string.Empty);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractHiddenIds_HiddenTrue_ReturnsId()
    {
        const string text = "profile.azure.hidden = true";
        var result = ProfileSourceParser.ExtractHiddenIds(text);
        Assert.Single(result);
        Assert.Contains("azure", result);
    }

    [Fact]
    public void ExtractHiddenIds_HiddenFalse_IsIgnored()
    {
        const string text = "profile.azure.hidden = false";
        var result = ProfileSourceParser.ExtractHiddenIds(text);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("profile.FOO.hidden = TRUE", "foo")]
    [InlineData("profile.Bar.HIDDEN = True", "bar")]
    public void ExtractHiddenIds_CaseInsensitive(string line, string expectedId)
    {
        var result = ProfileSourceParser.ExtractHiddenIds(line);
        Assert.Contains(expectedId, result);
    }

    [Fact]
    public void ExtractHiddenIds_StripsBom()
    {
        const string text = "\uFEFFprofile.azure.hidden = true";
        var result = ProfileSourceParser.ExtractHiddenIds(text);
        Assert.Contains("azure", result);
    }

    [Fact]
    public void ExtractHiddenIds_MixedWithOtherKeys_OnlyHiddenExtracted()
    {
        const string text = """
            profile.full.name = Full
            profile.full.command = cmd.exe
            profile.full.hidden = true
            profile.azure.hidden = true
            profile.visible.name = Visible
            profile.visible.command = pwsh.exe
            """;
        var result = ProfileSourceParser.ExtractHiddenIds(text);
        Assert.Equal(2, result.Count);
        Assert.Contains("full", result);
        Assert.Contains("azure", result);
    }

    [Fact]
    public void ExtractHiddenIds_IgnoresCommentsAndBlanks()
    {
        const string text = """

            # profile.ignored.hidden = true
            profile.real.hidden = true
            """;
        var result = ProfileSourceParser.ExtractHiddenIds(text);
        Assert.Single(result);
        Assert.Contains("real", result);
    }

    [Fact]
    public void ExtractHiddenIds_InvalidIdCharacters_AreIgnored()
    {
        // Same regex as Parse -- id must be [a-z0-9-]+
        const string text = "profile.BAD_ID.hidden = true";
        var result = ProfileSourceParser.ExtractHiddenIds(text);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractHiddenIds_NonHiddenSubkey_Ignored()
    {
        const string text = "profile.azure.name = Azure";
        var result = ProfileSourceParser.ExtractHiddenIds(text);
        Assert.Empty(result);
    }
}
