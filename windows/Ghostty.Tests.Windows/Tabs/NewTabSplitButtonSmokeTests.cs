using Xunit;

namespace Ghostty.Tests.Windows.Tabs;

public class NewTabSplitButtonSmokeTests
{
    [Fact]
    public void NewTabSplitButton_IsHostedInFooter_AndAddButtonIsHidden()
    {
        // Booting a real MainWindow inside a unit test requires the
        // XAML test host pattern used elsewhere in this project. If
        // the host is not yet established, this test is structured to
        // fail clearly with TODO guidance rather than silently pass.
        //
        // Required helpers (verify exist; create with InternalsVisibleTo
        // if they do not):
        //   - WinUITestHost.RunOnUiAsync(() => ...)
        //   - WinUITestHost.NewMainWindow(stubProfileRegistry)
        //   - WinUITestHost.FindDescendantOfType<T>(element)
        //
        // The body uses these helpers via DispatcherQueueController.
        // If they do not exist, this test is the right place to
        // introduce them -- the harness pattern is shared with future
        // PR 4.5 / PR 6 tests.

        Assert.Fail("TODO: wire WinUITestHost. See file header comment.");
    }
}
