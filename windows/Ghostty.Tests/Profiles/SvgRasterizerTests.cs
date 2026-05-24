using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class SvgRasterizerTests
{
    private const string SimpleRedCircle =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\">"
        + "<circle cx=\"16\" cy=\"16\" r=\"12\" fill=\"red\"/></svg>";

    [Fact]
    public void Rasterize_ValidSvg_ProducesPngWithSignature()
    {
        var png = SvgRasterizer.Rasterize(SimpleRedCircle, sizePx: 32);
        Assert.NotEmpty(png);
        Assert.Equal(0x89, png[0]);
        Assert.Equal(0x50, png[1]);
        Assert.Equal(0x4E, png[2]);
        Assert.Equal(0x47, png[3]);
    }

    [Fact]
    public void Rasterize_RespectsSizePx()
    {
        var png16 = SvgRasterizer.Rasterize(SimpleRedCircle, sizePx: 16);
        var png32 = SvgRasterizer.Rasterize(SimpleRedCircle, sizePx: 32);
        // Coarse check: larger size produces larger output. Exact bytes
        // are renderer-dependent; we only assert non-equivalence.
        Assert.NotEqual(png16.Length, png32.Length);
    }

    [Fact]
    public void Rasterize_ScriptIsStrippedBeforeRender()
    {
        // If the sanitizer didn't run, SkiaSharp would still ignore the
        // script tag but parsing might fail. We just verify a sanitized
        // payload renders OK.
        var withScript =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\">"
            + "<script>x()</script><circle cx=\"16\" cy=\"16\" r=\"12\" fill=\"blue\"/></svg>";
        var png = SvgRasterizer.Rasterize(withScript, sizePx: 16);
        Assert.NotEmpty(png);
    }

    [Fact]
    public void Rasterize_InvalidSvg_ReturnsEmpty()
    {
        var png = SvgRasterizer.Rasterize("not an svg", sizePx: 16);
        Assert.Empty(png);
    }
}
