using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public class UiaColorTests
{
    [Fact]
    public void ToColorRef_SwapsRgbToBgr()
    {
        // 0x00RRGGBB = 0x00112233 -> COLORREF 0x00BBGGRR = 0x00332211
        Assert.Equal(0x00332211, UiaColor.ToColorRef(0x00112233u));
    }

    [Fact]
    public void ToColorRef_Black_IsZero()
    {
        Assert.Equal(0x00000000, UiaColor.ToColorRef(0x00000000u));
    }

    [Fact]
    public void ToColorRef_White_IsWhite()
    {
        Assert.Equal(0x00FFFFFF, UiaColor.ToColorRef(0x00FFFFFFu));
    }
}
