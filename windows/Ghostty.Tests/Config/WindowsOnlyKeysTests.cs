using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

public class WindowsOnlyKeysTests
{
    [Theory]
    [InlineData("background-style")]
    [InlineData("background-tint-color")]
    [InlineData("background-tint-opacity")]
    [InlineData("background-luminosity-opacity")]
    [InlineData("background-blur-follows-opacity")]
    [InlineData("background-gradient-point")]
    [InlineData("background-gradient-animation")]
    [InlineData("background-gradient-speed")]
    [InlineData("background-gradient-blend")]
    [InlineData("background-gradient-opacity")]
    [InlineData("vertical-tabs")]
    [InlineData("command-palette-group-commands")]
    [InlineData("command-palette-background")]
    [InlineData("power-saver-mode")]
    [InlineData("default-profile")]
    [InlineData("profile-order")]
    [InlineData("no-color-override")]
    public void Contains_KnownKey(string key)
    {
        Assert.True(WindowsOnlyKeys.Contains(key));
    }

    [Fact]
    public void Contains_UpstreamKey_IsFalse()
    {
        // These are all in upstream Zig Config, not Windows-only.
        Assert.False(WindowsOnlyKeys.Contains("background-opacity"));
        Assert.False(WindowsOnlyKeys.Contains("font-size"));
        Assert.False(WindowsOnlyKeys.Contains("theme"));
        Assert.False(WindowsOnlyKeys.Contains("windows-settings-ui"));
    }

    [Fact]
    public void Contains_IsCaseInsensitive()
    {
        Assert.True(WindowsOnlyKeys.Contains("Background-Style"));
        Assert.True(WindowsOnlyKeys.Contains("BACKGROUND-STYLE"));
    }

    [Fact]
    public void AllEntries_HaveNonEmptyFields()
    {
        foreach (var entry in WindowsOnlyKeys.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Key));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
        }
    }

    [Theory]
    [InlineData("C:\\Users\\alex\\AppData\\Roaming\\com.mitchellh.ghostty\\config:42:background-style: unknown field", "background-style")]
    [InlineData("/home/alex/.config/ghostty/config:3:background-gradient-point: unknown field", "background-gradient-point")]
    [InlineData("cli:1:background-style: unknown field", "background-style")]
    [InlineData("background-style: unknown field", "background-style")]
    public void TryExtractUnknownFieldKey_ParsesKey(string message, string expected)
    {
        Assert.True(WindowsOnlyKeys.TryExtractUnknownFieldKey(message, out var key));
        Assert.Equal(expected, key);
    }

    [Fact]
    public void TryExtractUnknownFieldKey_EmptyKeyFormat_YieldsNonMatchingToken()
    {
        Assert.True(WindowsOnlyKeys.TryExtractUnknownFieldKey("cli:1: unknown field", out var key));
        Assert.Equal("1", key);
        Assert.False(WindowsOnlyKeys.Contains(key));
    }

    [Theory]
    [InlineData("config:42:font-family: value required")]
    [InlineData("config:42:theme: invalid value \"nope\"")]
    [InlineData("some unrelated log line")]
    [InlineData("")]
    public void TryExtractUnknownFieldKey_NonMatch(string message)
    {
        Assert.False(WindowsOnlyKeys.TryExtractUnknownFieldKey(message, out var key));
        Assert.Equal(string.Empty, key);
    }

    [Fact]
    public void TryExtractUnknownFieldKey_HandlesNull()
    {
        Assert.False(WindowsOnlyKeys.TryExtractUnknownFieldKey(null!, out var key));
        Assert.Equal(string.Empty, key);
    }

    [Fact]
    public void All_Contains_DefaultProfile()
    {
        Assert.Contains(Ghostty.Core.Config.WindowsOnlyKeys.All,
                        e => e.Key == "default-profile");
    }

    [Fact]
    public void All_Contains_ProfileOrder()
    {
        Assert.Contains(Ghostty.Core.Config.WindowsOnlyKeys.All,
                        e => e.Key == "profile-order");
    }

    [Theory]
    [InlineData("profile.foo.name", true)]
    [InlineData("profile.foo.command", true)]
    [InlineData("profile.foo.hidden", true)]
    [InlineData("profile.a.b", true)]                   // minimal valid
    [InlineData("PROFILE.FOO.NAME", true)]              // case-insensitive prefix
    [InlineData("profile.foo", false)]                  // missing subkey
    [InlineData("profile.", false)]                     // missing id + subkey
    [InlineData("profile", false)]                      // exact scalar
    [InlineData("profile..bar", false)]                 // empty id
    [InlineData("profileorder", false)]                 // no dot
    [InlineData("default-profile", false)]              // exact scalar, not subkey
    public void IsProfileSubkey_Expected(string key, bool expected)
    {
        Assert.Equal(expected, Ghostty.Core.Config.WindowsOnlyKeys.IsProfileSubkey(key));
    }

    [Theory]
    [InlineData("internal.update-simulator", true)]
    [InlineData("internal.foo", true)]
    [InlineData("internal.a", true)]                    // minimal valid (single char name)
    [InlineData("INTERNAL.UPDATE-SIMULATOR", true)]     // case-insensitive prefix
    [InlineData("internal.", false)]                    // bare prefix, no name
    [InlineData("internal", false)]                     // exact scalar
    [InlineData("internalish", false)]                  // prefix-without-dot, different key
    [InlineData("profile.x.internal", false)]           // different namespace
    [InlineData("background-style", false)]             // unrelated public key
    public void IsInternalKey_Expected(string key, bool expected)
    {
        Assert.Equal(expected, Ghostty.Core.Config.WindowsOnlyKeys.IsInternalKey(key));
    }

    [Theory]
    [InlineData("agent-detect.mybot", true)]
    [InlineData("agent-detect.claude", true)]
    [InlineData("agent-detect.a", true)]                // minimal valid (single char name)
    [InlineData("agent-detect.my.bot", true)]           // dotted name: reader takes key[prefix..] verbatim
    [InlineData("AGENT-DETECT.MYBOT", true)]            // case-insensitive prefix
    [InlineData("agent-detect.", false)]                // bare prefix, no name
    [InlineData("agent-detect", false)]                 // exact scalar
    [InlineData("agent-detectish", false)]              // prefix-without-dot, different key
    [InlineData("profile.x.agent-detect", false)]       // different namespace
    [InlineData("background-style", false)]             // unrelated public key
    public void IsAgentDetectKey_Expected(string key, bool expected)
    {
        Assert.Equal(expected, Ghostty.Core.Config.WindowsOnlyKeys.IsAgentDetectKey(key));
    }

    [Fact]
    public void AgentDetectDiagnostic_ExtractsKey_AndClassifiesForSilentSuppression()
    {
        // A saved agent detector (agent-detect.mybot = process=mybot) is an
        // unknown field to libghostty's parser, which emits this diagnostic.
        // The filter must recognize the extracted key as an agent-detect key
        // so ConfigService can suppress it -- and it must NOT be a first-class
        // Windows-only key (those surface via WindowsOnlyKeysUsed; agent
        // detectors are per-row repeatable and stay silent like internal.*).
        const string message =
            "C:\\Users\\alex\\AppData\\Roaming\\com.mitchellh.ghostty\\config:5:agent-detect.mybot: unknown field";

        Assert.True(WindowsOnlyKeys.TryExtractUnknownFieldKey(message, out var key));
        Assert.Equal("agent-detect.mybot", key);
        Assert.True(WindowsOnlyKeys.IsAgentDetectKey(key));
        Assert.False(WindowsOnlyKeys.Contains(key));
    }
}
