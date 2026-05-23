using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

public class MouseVisibilityEnumTests
{
    // Pin the C-enum ordinals so a future libghostty reorder cannot
    // silently swap visible/hidden. Mirrors
    // include/ghostty.h ghostty_action_mouse_visibility_e:
    //   GHOSTTY_MOUSE_VISIBLE = 0,
    //   GHOSTTY_MOUSE_HIDDEN  = 1,
    [Theory]
    [InlineData(MouseVisibility.Visible, 0)]
    [InlineData(MouseVisibility.Hidden, 1)]
    public void Enum_OrdinalsMatchGhosttyH(MouseVisibility value, int expected) =>
        Assert.Equal(expected, (int)value);
}
