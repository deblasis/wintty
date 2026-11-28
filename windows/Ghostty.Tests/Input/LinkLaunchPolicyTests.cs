using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

public class LinkLaunchPolicyTests
{
    [Theory]
    [InlineData("https://ghostty.org/docs")]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM/")]
    [InlineData("mailto:someone@example.com")]
    public void WebAndMailLinks_OpenDirectly(string url)
    {
        Assert.Equal(LinkLaunchDecision.Open, LinkLaunchPolicy.Decide(url, out var uri));
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("search-ms:query=x&crumb=location:\\\\attacker\\share")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("ms-appinstaller:?source=https://evil.example/app.appinstaller")]
    [InlineData("file:///C:/Users/x/Downloads/run.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("ssh://devel.local")]
    [InlineData("ftp://files.example.com")]
    public void OtherSchemes_NeedConfirmation(string url)
    {
        Assert.Equal(LinkLaunchDecision.Confirm, LinkLaunchPolicy.Decide(url, out var uri));
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void NonAbsolute_IsRefused(string? url)
    {
        Assert.Equal(LinkLaunchDecision.Refuse, LinkLaunchPolicy.Decide(url, out var uri));
        Assert.Null(uri);
    }
}
