using Xunit;

namespace Ghostty.Tests.Windows.Tabs;

public class NewTabSplitButtonSmokeTests
{
    // Booting a real MainWindow inside a unit test requires the XAML
    // test host pattern used elsewhere in this project.
    //
    // Required helpers (verify exist; create with InternalsVisibleTo
    // if they do not):
    //   - WinUITestHost.RunOnUiAsync(() => ...)
    //   - WinUITestHost.NewMainWindow(stubProfileRegistry)
    //   - WinUITestHost.FindDescendantOfType<T>(element)
    //
    // The body uses these helpers via DispatcherQueueController. If
    // they do not exist, this test is the right place to introduce
    // them -- the harness pattern is shared with future PR 4.5 / PR 6
    // tests.
    [Fact(Skip = "TODO: wire WinUITestHost. See file header comment.")]
    public void NewTabSplitButton_IsHostedInFooter_AndAddButtonIsHidden()
    {
    }
}
