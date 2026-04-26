using Xunit;

namespace Ghostty.Tests.Windows.Settings;

public class ProfilesPageSmokeTests
{
    // Wire-up plan when WinUITestHost lands:
    //   1. Build a SettingsWindow on the WinUITestHost dispatcher.
    //   2. Inject a FakeProfileRegistry with 2 visible + 1 hidden
    //      profile and a no-op IConfigFileEditor.
    //   3. Navigate to the "profiles" tab.
    //   4. Assert ProfilesGroup.Cards.Count == 3 with toggle states
    //      off/off/on in registry-order.
    //   5. Toggle the first row on; assert the editor saw a
    //      SetValue("profile.<id>.hidden", "true") call.
    //   6. Toggle the third row off; assert the editor saw a
    //      RemoveValue("profile.<id>.hidden") call.
    [Fact(Skip = "TODO: wire WinUITestHost (also blocks PR 4 tasks 17/18 + PR 5 chord smoke)")]
    public void ProfilesPage_RendersVisibleAndHiddenProfilesInOneFlatList()
    {
    }
}
