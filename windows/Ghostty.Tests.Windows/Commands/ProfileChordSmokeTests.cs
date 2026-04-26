using Xunit;

namespace Ghostty.Tests.Windows.Commands;

public class ProfileChordSmokeTests
{
    // Manual smoke spec for the Ctrl+Shift+N profile chord.
    //
    // Slot-to-id resolution is already covered exhaustively by
    // ProfileSlotResolverTests in Ghostty.Tests. The piece this
    // test would exercise -- the WinUI keyboard accelerator into
    // PaneAction routing -- requires a real MainWindow on a
    // dispatcher, which is the same architectural blocker that
    // keeps NewTabSplitButtonSmokeTests un-automated.
    //
    // To validate by hand: with 3 profiles ("a","b","c") in the
    // config, press Ctrl+Shift+2 and confirm a new tab opens for
    // profile "b".
    [Fact(Skip = "Manual smoke; XAML+dispatcher hosting not in scope for this test project.")]
    public void CtrlShift2_With3Profiles_OpensProfileAtIndex1AsNewTab()
    {
    }
}
