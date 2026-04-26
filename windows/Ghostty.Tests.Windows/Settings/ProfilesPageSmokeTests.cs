using Xunit;

namespace Ghostty.Tests.Windows.Settings;

public class ProfilesPageSmokeTests
{
    // Manual smoke spec for the Profiles settings page.
    //
    // Visible/hidden ordering and per-row hide-marker parsing are
    // already covered by ProfileOrderResolverTests,
    // ProfileRegistryTests, ProfileHiddenKeyTests, and
    // ProfileSourceParserHiddenIdsTests in Ghostty.Tests. What this
    // test would add is the SettingsCard rendering and the
    // ToggleSwitch into IConfigFileEditor write path inside a real
    // SettingsWindow on a dispatcher, and hosting WinUI 3 here is
    // the same architectural blocker that keeps the other smoke
    // tests un-automated.
    //
    // To validate by hand:
    //   1. Open Settings -> Profiles with 2 visible + 1 hidden
    //      profile.
    //   2. Confirm 3 rows render in registry order with toggle
    //      states off/off/on.
    //   3. Toggle the first row on; confirm the config file gains
    //      'profile.<id>.hidden = true'.
    //   4. Toggle the third row off; confirm the line is removed
    //      via RemoveValue (not rewritten as 'hidden = false').
    [Fact(Skip = "Manual smoke; XAML+dispatcher hosting not in scope for this test project.")]
    public void ProfilesPage_RendersVisibleAndHiddenProfilesInOneFlatList()
    {
    }
}
