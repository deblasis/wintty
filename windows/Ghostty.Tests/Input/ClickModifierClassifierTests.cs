using Ghostty.Core.Input;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Input;

public class ClickModifierClassifierTests
{
    [Fact]
    public void Classify_NoModifier_ReturnsNewTab()
    {
        var mods = new FakeModifierKeyState();
        Assert.Equal(ProfileLaunchTarget.NewTab, ClickModifierClassifier.Classify(mods));
    }

    [Fact]
    public void Classify_AltOnly_ReturnsNewPane()
    {
        var mods = new FakeModifierKeyState { IsAltDown = true };
        Assert.Equal(ProfileLaunchTarget.NewPane, ClickModifierClassifier.Classify(mods));
    }

    [Theory]
    [InlineData(false, true)]   // Shift only
    [InlineData(true, true)]    // Alt + Shift  (Shift wins)
    public void Classify_ShiftWins_ReturnsNewWindow(bool alt, bool shift)
    {
        var mods = new FakeModifierKeyState { IsAltDown = alt, IsShiftDown = shift };
        Assert.Equal(ProfileLaunchTarget.NewWindow, ClickModifierClassifier.Classify(mods));
    }
}
