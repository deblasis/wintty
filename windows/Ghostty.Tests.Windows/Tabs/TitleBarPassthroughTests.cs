using Xunit;

namespace Ghostty.Tests.Windows.Tabs;

public class TitleBarPassthroughTests
{
    [Fact]
    public void SplitButton_BoundsAreNotInsideTitleBarElement()
    {
        // Same WinUITestHost helper as NewTabSplitButtonSmokeTests.
        // Assertions:
        //   1. Find the SplitButton ('ButtonRoot') inside MainWindow.
        //   2. Find the element passed to AppWindow.SetTitleBar
        //      (TabHost.CustomDragRegion = the cell-1 spacer).
        //   3. Compute SplitButton.TransformToVisual(spacer)
        //      .TransformBounds(Rect.Empty).
        //   4. Assert the SplitButton bounds are NOT contained within
        //      the spacer's bounds (i.e. they live in cell 0, not cell 1).

        Assert.Fail("TODO: wire WinUITestHost (shared with NewTabSplitButtonSmokeTests).");
    }
}
