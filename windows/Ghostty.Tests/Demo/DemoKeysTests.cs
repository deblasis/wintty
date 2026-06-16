#if DEMO
using Ghostty.Core.Demo;
using Xunit;

namespace Ghostty.Tests.Demo;

public class DemoKeysTests
{
    private static readonly string Esc = ((char)0x1b).ToString();
    private static readonly string Del = ((char)0x7f).ToString();

    [Theory]
    [InlineData("enter", "\r")]
    [InlineData("return", "\r")]
    [InlineData("tab", "\t")]
    public void Resolve_SimpleKeys(string name, string expected)
    {
        Assert.Equal(expected, DemoKeys.Resolve(name));
    }

    [Fact]
    public void Resolve_EscapeAndBackspace()
    {
        Assert.Equal(Esc, DemoKeys.Resolve("escape"));
        Assert.Equal(Esc, DemoKeys.Resolve("esc"));
        Assert.Equal(Del, DemoKeys.Resolve("backspace"));
    }

    [Fact]
    public void Resolve_Arrows_HaveEscPrefix()
    {
        Assert.Equal(Esc + "[A", DemoKeys.Resolve("up"));
        Assert.Equal(Esc + "[B", DemoKeys.Resolve("down"));
        Assert.Equal(Esc + "[C", DemoKeys.Resolve("right"));
        Assert.Equal(Esc + "[D", DemoKeys.Resolve("left"));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        Assert.Equal("\r", DemoKeys.Resolve("ENTER"));
        Assert.Equal(DemoKeys.Resolve("up"), DemoKeys.Resolve("UP"));
    }

    [Fact]
    public void Resolve_UnknownKey_ReturnsNull()
    {
        Assert.Null(DemoKeys.Resolve("f13"));
        Assert.Null(DemoKeys.Resolve(null));
    }
}
#endif
