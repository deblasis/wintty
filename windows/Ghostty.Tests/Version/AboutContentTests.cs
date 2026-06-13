using System;
using Ghostty.Core.Version;
using Xunit;

namespace Ghostty.Tests.Version;

public class AboutContentTests
{
    [Theory]
    [InlineData("https://github.com/deblasis/wintty")]
    [InlineData("https://wintty.io/docs")]
    [InlineData("https://wintty.io")]
    [InlineData("https://github.com/sponsors/deblasis")]
    public void Urls_AreAbsoluteHttps(string url)
    {
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var parsed));
        Assert.Equal(Uri.UriSchemeHttps, parsed!.Scheme);
    }

    [Fact]
    public void Urls_MatchExpectedTargets()
    {
        Assert.Equal("https://github.com/deblasis/wintty", AboutContent.GitHubUrl);
        Assert.Equal("https://wintty.io/docs", AboutContent.DocsUrl);
        Assert.Equal("https://wintty.io", AboutContent.HomepageUrl);
        Assert.Equal("https://github.com/sponsors/deblasis", AboutContent.SponsorUrl);
    }

    [Fact]
    public void Tagline_IsNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(AboutContent.Tagline));
    }

    [Fact]
    public void License_MentionsMitAndContributors()
    {
        Assert.Contains("MIT", AboutContent.LicenseNote);
        Assert.Contains("Ghostty contributors", AboutContent.Copyright);
    }
}
