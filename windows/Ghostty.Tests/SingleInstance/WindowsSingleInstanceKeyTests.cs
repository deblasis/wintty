using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

public sealed class WindowsSingleInstanceKeyTests
{
    [Fact]
    public void Key_IsRegisteredAsWindowsOnly()
    {
        // Dev-only escape hatch (#1094): the key stays registered so an
        // existing config carrying `windows-single-instance = false` keeps
        // parsing without an unknown-field diagnostic, but it is no longer
        // offered in the settings UI.
        Assert.True(WindowsOnlyKeys.Contains("windows-single-instance"));
    }

    [Fact]
    public void Key_HasDescription()
    {
        Assert.True(WindowsOnlyKeys.ByKey.TryGetValue("windows-single-instance", out var entry));
        Assert.False(string.IsNullOrWhiteSpace(entry.Description));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("", true)]       // unset => default ON (#1094)
    [InlineData("1", true)]      // unrecognized spelling falls back to the default
    public void ParseBool_MatchesGateSemantics(string raw, bool expected)
    {
        Assert.Equal(expected, WindowsOnlyKeyParsers.ParseBool(raw, defaultValue: true));
    }

    /// <summary>
    /// The decision that makes #1094: the pre-Application.Start read in
    /// Program must default ON, because multiple windows make sense and
    /// multiple processes do not. A user's unset key elects the primary.
    /// </summary>
    [Fact]
    public void TheEarlyRead_DefaultsToOn()
    {
        var reader = Wiring.ShellSource.Load("Program.cs")
            .Method("ReadSingleInstanceSetting");

        Assert.Contains("defaultValue: true", reader.Body!.ToString());
    }
}
