using Xunit;

namespace Ghostty.Tests.Windows.Tabs;

public class TitleBarPassthroughTests
{
    // Manual smoke spec for split-button title-bar passthrough.
    //
    // Same architectural blocker as NewTabSplitButtonSmokeTests:
    // verifying the SplitButton bounds sit outside the title-bar
    // drag region requires a live MainWindow on a dispatcher,
    // which is outside the contract of Ghostty.Tests.Windows
    // (Core-only references; no WinUI host).
    //
    // To validate by hand:
    //   1. Run the app.
    //   2. Drag the window from the area just left of the new-tab
    //      split button. The drag should succeed (the spacer cell
    //      is the title-bar element).
    //   3. Click the same split button. It should activate the
    //      new-tab dropdown without dragging the window.
    [Fact(Skip = "Manual smoke; XAML+dispatcher hosting not in scope for this test project.")]
    public void SplitButton_BoundsAreNotInsideTitleBarElement()
    {
    }
}
