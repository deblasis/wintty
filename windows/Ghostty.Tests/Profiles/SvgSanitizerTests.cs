using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class SvgSanitizerTests
{
    [Fact]
    public void Sanitize_StripsScriptElements()
    {
        var dirty = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script><circle r=\"1\"/></svg>";
        var clean = SvgSanitizer.Sanitize(dirty);
        Assert.DoesNotContain("<script", clean);
        Assert.Contains("<circle", clean);
    }

    [Fact]
    public void Sanitize_StripsForeignObject()
    {
        var dirty = "<svg xmlns=\"http://www.w3.org/2000/svg\"><foreignObject><iframe src=\"x\"/></foreignObject><rect/></svg>";
        var clean = SvgSanitizer.Sanitize(dirty);
        Assert.DoesNotContain("foreignObject", clean);
        Assert.DoesNotContain("iframe", clean);
        Assert.Contains("<rect", clean);
    }

    [Fact]
    public void Sanitize_StripsEventHandlerAttributes()
    {
        var dirty = "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect onclick=\"alert(1)\" onmouseover=\"x()\" fill=\"red\"/></svg>";
        var clean = SvgSanitizer.Sanitize(dirty);
        Assert.DoesNotContain("onclick", clean);
        Assert.DoesNotContain("onmouseover", clean);
        Assert.Contains("fill=\"red\"", clean);
    }

    [Fact]
    public void Sanitize_StripsAnchorAndExternalHrefs()
    {
        var dirty = "<svg xmlns=\"http://www.w3.org/2000/svg\"><a href=\"https://evil\"><rect/></a><use xlink:href=\"http://evil/x.svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"/></svg>";
        var clean = SvgSanitizer.Sanitize(dirty);
        Assert.DoesNotContain("<a ", clean);
        Assert.DoesNotContain("evil", clean);
    }

    [Fact]
    public void Sanitize_PreservesDataUriHrefs()
    {
        var dirty = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><image xlink:href=\"data:image/png;base64,AA==\" width=\"1\" height=\"1\"/></svg>";
        var clean = SvgSanitizer.Sanitize(dirty);
        Assert.Contains("data:image/png;base64", clean);
    }

    [Fact]
    public void Sanitize_InvalidXml_ReturnsEmptyString()
    {
        // Defensive: malformed input must not throw. Empty output causes
        // the rasterizer to produce a transparent PNG and the caller falls
        // back to default.
        var clean = SvgSanitizer.Sanitize("<svg<<<broken");
        Assert.Equal(string.Empty, clean);
    }
}
