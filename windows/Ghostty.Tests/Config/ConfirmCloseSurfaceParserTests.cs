using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// TabHost/VerticalTabHost hardcoded confirm-close-multi-pane=true and
/// ignored the upstream <c>confirm-close-surface</c> key (false/true/always).
/// </summary>
public class ConfirmCloseSurfaceParserTests
{
    [Theory]
    [InlineData(null, ConfirmCloseSurfaceMode.True)]
    [InlineData("", ConfirmCloseSurfaceMode.True)]
    [InlineData("true", ConfirmCloseSurfaceMode.True)]
    [InlineData("TRUE", ConfirmCloseSurfaceMode.True)]
    [InlineData("false", ConfirmCloseSurfaceMode.False)]
    [InlineData("always", ConfirmCloseSurfaceMode.Always)]
    [InlineData("nope", ConfirmCloseSurfaceMode.True)]
    public void Parse_Normalizes(string? raw, ConfirmCloseSurfaceMode expected)
        => Assert.Equal(expected, ConfirmCloseSurfaceParser.Parse(raw));

    [Theory]
    [InlineData(ConfirmCloseSurfaceMode.False, 1, false)]
    [InlineData(ConfirmCloseSurfaceMode.False, 3, false)]
    [InlineData(ConfirmCloseSurfaceMode.True, 1, false)]
    [InlineData(ConfirmCloseSurfaceMode.True, 2, true)]
    [InlineData(ConfirmCloseSurfaceMode.Always, 1, true)]
    [InlineData(ConfirmCloseSurfaceMode.Always, 2, true)]
    public void ShouldConfirmTabClose(ConfirmCloseSurfaceMode mode, int panes, bool expected)
        => Assert.Equal(expected, ConfirmCloseSurfaceParser.ShouldConfirmTabClose(mode, panes));
}
