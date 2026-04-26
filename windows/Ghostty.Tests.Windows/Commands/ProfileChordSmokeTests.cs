using Xunit;

namespace Ghostty.Tests.Windows.Commands;

public class ProfileChordSmokeTests
{
    [Fact]
    public void CtrlShift2_With3Profiles_OpensProfileAtIndex1AsNewTab()
    {
        // Wire-up plan when WinUITestHost lands:
        //   1. Build a MainWindow on the WinUITestHost dispatcher.
        //   2. Inject a FakeProfileRegistry with 3 profiles ("a", "b", "c").
        //   3. Synthesize a Ctrl+Shift+Number2 KeyboardAccelerator invocation.
        //   4. Assert OpenProfile was called with id="b", target=NewTab.
        //
        // Until WinUITestHost exists (also blocking PR 4 tasks 17/18), this
        // stub fails so the test surface is visible in CI counts and reviewers
        // can grep for the string.
        Assert.Fail("TODO: wire WinUITestHost (also blocks PR 4 tasks 17/18)");
    }
}
