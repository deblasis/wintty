using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// The overload ConfigService.ReadFlagsCore is switching to (consuming
    /// the already-populated <see cref="ConfigIniFile"/> pairs cache instead
    /// of a second <c>File.ReadAllText</c> of the same file) has to produce
    /// the same <see cref="ProfileView"/> the old text-based call did, for
    /// the same underlying file content. This fixture exercises every knob
    /// called out in the redundant-read diagnosis: multiple profiles,
    /// duplicate subkey lines (last wins), values with surrounding spaces,
    /// a hidden id (true), a hidden-mention-only id (false, still
    /// suppresses its own missing-key warning), comment and blank lines, a
    /// non-profile key, and a mixed-case "Profile." key.
    /// </summary>
    [Fact]
    public void ParseAll_TextAndPairsCache_ProduceIdenticalResults()
    {
        const string text = """
            # a leading comment

            profile.web.name = Web Browser
            profile.web.command = firefox.exe
            profile.web.command = chrome.exe
            profile.mail.name =   Mail Client
            profile.mail.command = outlook.exe
            profile.mail.hidden = true

            profile.archived.hidden = false
            Profile.CASED.name = Cased Profile
            Profile.CASED.command = cased.exe
            font-family = Cascadia Code
            """;
        var values = new Dictionary<string, string?>
        {
            ["default-profile"] = "web",
            // Deliberately leaves "cased" uncovered: ProfileOrderResolver.
            // ResolveDefault falls back to ParsedProfiles enumeration order
            // when profile-order does not disambiguate, so this fixture only
            // exercises that fallback path if profile-order is partial.
            ["profile-order"] = "mail, web",
        };

        var fromText = ConfigServiceProfileParser.ParseAll(text, key => FileValue(values, key));
        var pairs = ConfigIniFile.ParseText(text);
        var fromPairs = ConfigServiceProfileParser.ParseAll(pairs, key => FileValue(values, key));

        Assert.Equal(fromText.ParsedProfiles.Count, fromPairs.ParsedProfiles.Count);
        foreach (var (id, def) in fromText.ParsedProfiles)
        {
            Assert.True(fromPairs.ParsedProfiles.TryGetValue(id, out var otherDef));
            Assert.Equal(def, otherDef);
        }
        // Order-preserving, not just membership: ProfileOrderResolver.
        // ResolveDefault falls back to ParsedProfiles enumeration order when
        // profile-order does not disambiguate, so the pairs path must yield
        // the same key sequence as the text path, first valid line wins.
        Assert.Equal(fromText.ParsedProfiles.Keys.ToList(), fromPairs.ParsedProfiles.Keys.ToList());
        Assert.Equal(fromText.ProfileOrder, fromPairs.ProfileOrder);
        Assert.Equal(fromText.DefaultProfileId, fromPairs.DefaultProfileId);
        // Set-compared: iteration order over a HashSet is not itself a
        // behavioral guarantee either path makes.
        Assert.Equal(
            fromText.HiddenProfileIds.OrderBy(x => x, System.StringComparer.Ordinal),
            fromPairs.HiddenProfileIds.OrderBy(x => x, System.StringComparer.Ordinal));
        Assert.Equal(
            fromText.ProfileWarnings.OrderBy(x => x, System.StringComparer.Ordinal),
            fromPairs.ProfileWarnings.OrderBy(x => x, System.StringComparer.Ordinal));

        // Pin the concrete values, not just that the two paths agree with
        // each other -- two paths agreeing on the wrong answer would still
        // pass the assertions above.
        Assert.Equal("chrome.exe", fromPairs.ParsedProfiles["web"].Command); // duplicate subkey, last wins
        Assert.Equal("Mail Client", fromPairs.ParsedProfiles["mail"].Name); // surrounding spaces trimmed
        Assert.Contains("mail", fromPairs.HiddenProfileIds); // hidden = true
        Assert.DoesNotContain("archived", fromPairs.HiddenProfileIds); // hidden = false
        Assert.Empty(fromPairs.ProfileWarnings); // archived's missing-name warning is suppressed
        Assert.Equal("Cased Profile", fromPairs.ParsedProfiles["cased"].Name); // "Profile." (mixed case) still matches
    }
}
