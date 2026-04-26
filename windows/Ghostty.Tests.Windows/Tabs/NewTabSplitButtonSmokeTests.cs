using Xunit;

namespace Ghostty.Tests.Windows.Tabs;

public class NewTabSplitButtonSmokeTests
{
    // Manual smoke spec for the new-tab split button placement.
    //
    // Not automated: hosting a real MainWindow inside this test
    // project would require Microsoft.WindowsAppSDK + a XAML
    // dispatcher host + a ProjectReference to Ghostty.csproj, and
    // MainWindow's ctor calls into libghostty via GhosttyHost.
    // Ghostty.Tests.Windows intentionally references only
    // Ghostty.Core, so neither dependency is acceptable here.
    //
    // To validate by hand:
    //   1. Run the app.
    //   2. Confirm the new-tab control is hosted in the tab footer
    //      and the legacy add-button is hidden.
    [Fact(Skip = "Manual smoke; XAML+dispatcher hosting not in scope for this test project.")]
    public void NewTabSplitButton_IsHostedInFooter_AndAddButtonIsHidden()
    {
    }
}
