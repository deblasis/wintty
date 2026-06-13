using Ghostty.Core.Bell;
using Xunit;

namespace Ghostty.Tests.Bell;

public class BellFeaturesTests
{
    [Fact]
    public void FromBits_Zero_AllFalse()
    {
        var f = BellFeatures.FromBits(0);
        Assert.False(f.System);
        Assert.False(f.Audio);
        Assert.False(f.Attention);
        Assert.False(f.Title);
        Assert.False(f.Border);
        Assert.True(f.None);
    }

    [Theory]
    [InlineData(1u, true, false, false, false, false)]   // system
    [InlineData(2u, false, true, false, false, false)]   // audio
    [InlineData(4u, false, false, true, false, false)]   // attention
    [InlineData(8u, false, false, false, true, false)]   // title
    [InlineData(16u, false, false, false, false, true)]  // border
    public void FromBits_SingleBit_DecodesCorrectField(
        uint bits, bool system, bool audio, bool attention, bool title, bool border)
    {
        var f = BellFeatures.FromBits(bits);
        Assert.Equal(system, f.System);
        Assert.Equal(audio, f.Audio);
        Assert.Equal(attention, f.Attention);
        Assert.Equal(title, f.Title);
        Assert.Equal(border, f.Border);
    }

    [Fact]
    public void FromBits_StockDefault_IsAttentionAndTitle()
    {
        // Upstream BellFeatures default = attention,title = 0b01100 = 12.
        var f = BellFeatures.FromBits(12);
        Assert.True(f.Attention);
        Assert.True(f.Title);
        Assert.False(f.System);
        Assert.False(f.Audio);
        Assert.False(f.Border);
        Assert.False(f.None);
    }

    [Fact]
    public void FromBits_AllBits_AllTrue()
    {
        var f = BellFeatures.FromBits(31);
        Assert.True(f.System && f.Audio && f.Attention && f.Title && f.Border);
    }

    [Fact]
    public void FromBits_IgnoresHighBitsOutsideContract()
    {
        // A spurious high bit must not flip any decoded field.
        var f = BellFeatures.FromBits(0xFFFF_FFE0);
        Assert.True(f.None);
    }
}
