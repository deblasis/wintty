using System.Collections.Generic;
using Ghostty.Core.Config;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Config;

public class ConfigServiceProfileParserTests
{
    private static string? FileValue(IReadOnlyDictionary<string, string?> bag, string key)
        => bag.TryGetValue(key, out var v) ? v : null;

    [Fact]
    public void ParseAll_EmptyInputs_ReturnsEmptyView()
    {
        var values = new Dictionary<string, string?>();
        var result = ConfigServiceProfileParser.ParseAll(
            string.Empty, key => FileValue(values, key));

        Assert.Empty(result.ParsedProfiles);
        Assert.Empty(result.ProfileOrder);
        Assert.Null(result.DefaultProfileId);
        Assert.Empty(result.HiddenProfileIds);
        Assert.Empty(result.ProfileWarnings);
    }

    [Fact]
    public void ParseAll_TwoProfiles_ParsedAndOrderedAndDefault()
    {
        const string text = """
            profile.a.name = A
            profile.a.command = cmd.exe
            profile.b.name = B
            profile.b.command = pwsh.exe
            """;
        var values = new Dictionary<string, string?>
        {
            ["default-profile"] = "a",
            ["profile-order"] = "b, a",
        };

        var result = ConfigServiceProfileParser.ParseAll(text, key => FileValue(values, key));

        Assert.Equal(2, result.ParsedProfiles.Count);
        Assert.Contains("a", result.ParsedProfiles.Keys);
        Assert.Contains("b", result.ParsedProfiles.Keys);
        Assert.Equal("a", result.DefaultProfileId);
        Assert.Equal(new[] { "b", "a" }, result.ProfileOrder);
        Assert.Empty(result.HiddenProfileIds);
    }

    [Fact]
    public void ParseAll_HiddenOnlyOverride_PopulatesHiddenSet()
    {
        const string text = "profile.azure.hidden = true";
        var values = new Dictionary<string, string?>();

        var result = ConfigServiceProfileParser.ParseAll(text, key => FileValue(values, key));

        Assert.Contains("azure", result.HiddenProfileIds);
        Assert.Empty(result.ParsedProfiles);  // hidden-only is not a full def
        Assert.Empty(result.ProfileWarnings);
    }

    [Fact]
    public void ParseAll_HiddenFalseOnlyOverride_DoesNotProduceWarning()
    {
        // The settings-page un-hide path writes hidden = false. An id
        // with only that line is still a hide-override marker (the user
        // is explicitly opting back in), not a malformed profile, so
        // no missing-name warning should surface.
        const string text = "profile.pwsh-7.hidden = false";
        var values = new Dictionary<string, string?>();

        var result = ConfigServiceProfileParser.ParseAll(text, key => FileValue(values, key));

        Assert.DoesNotContain("pwsh-7", result.HiddenProfileIds);
        Assert.Empty(result.ParsedProfiles);
        Assert.Empty(result.ProfileWarnings);
    }

    [Fact]
    public void ParseAll_MalformedProfile_ProducesWarning()
    {
        const string text = "profile.broken.name = NoCommand";  // missing command
        var values = new Dictionary<string, string?>();

        var result = ConfigServiceProfileParser.ParseAll(text, key => FileValue(values, key));

        Assert.Empty(result.ParsedProfiles);
        Assert.Single(result.ProfileWarnings);
        Assert.Contains("broken", result.ProfileWarnings[0]);
    }

    [Fact]
    public void ParseAll_ProfileOrderWithExtraWhitespace_IsTrimmed()
    {
        var values = new Dictionary<string, string?>
        {
            ["profile-order"] = "  a ,b  ,   c",
        };

        var result = ConfigServiceProfileParser.ParseAll(string.Empty, key => FileValue(values, key));

        Assert.Equal(new[] { "a", "b", "c" }, result.ProfileOrder);
    }

    [Fact]
    public void ParseAll_EmptyProfileOrderString_YieldsEmptyList()
    {
        var values = new Dictionary<string, string?>
        {
            ["profile-order"] = "",
        };

        var result = ConfigServiceProfileParser.ParseAll(string.Empty, key => FileValue(values, key));

        Assert.Empty(result.ProfileOrder);
    }

    [Fact]
    public void ParseAll_DefaultProfileEmptyString_IsNull()
    {
        var values = new Dictionary<string, string?>
        {
            ["default-profile"] = "",
        };

        var result = ConfigServiceProfileParser.ParseAll(string.Empty, key => FileValue(values, key));

        Assert.Null(result.DefaultProfileId);
    }

    [Fact]
    public void ParseAll_HiddenIdIsSubstringOfMalformedId_DoesNotSuppressWarning()
    {
        const string text = """
            profile.bro.hidden = true
            profile.broken.name = NoCommand
            """;
        var values = new Dictionary<string, string?>();

        var result = ConfigServiceProfileParser.ParseAll(text, key => FileValue(values, key));

        Assert.Contains("bro", result.HiddenProfileIds);
        Assert.Single(result.ProfileWarnings);
        Assert.Contains("broken", result.ProfileWarnings[0]);
    }
}
