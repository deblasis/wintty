using System;
using System.Linq;
using Ghostty.Core.Config;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

/// <summary>
/// P1-6, quoted verbatim from the brief:
///
/// "PARTIAL PROFILE BLOCKS ARE DROPPED - the settings page writes exactly
/// one subkey (ProfilesPage.xaml.cs ~295-312 icon, ~333-338 tracks), but
/// Ghostty.Core/Profiles/ProfileSourceParser.cs ~138-150 drops any
/// profile.&lt;id&gt;.* bag lacking name (then command) and warns 'missing
/// required key 'name', dropped'; the merge then never sees the override
/// (ProfileOrderResolver.cs ~33-54 adds the discovered def only if absent),
/// so the value snaps back on reload."
///
/// The seam under test is the one a config reload actually walks, start to
/// finish: the <see cref="ProfileSourceParser"/> bag, the
/// <see cref="ConfigServiceProfileParser.ProfileView"/> that carries it, and
/// the <see cref="ProfileOrderResolver"/> composition that turns it into
/// what the settings page renders. The page's own contribution is one
/// <c>_writer.Write(() =&gt; _editor.SetValue(...))</c>, so what it puts on
/// disk is exactly the config text these fixtures hand the parser.
///
/// Both keys the page writes are covered: the icon
/// (<c>OnProfileIconChanged</c>) and the per-profile icon-tracking freeze
/// (<c>OnTracksForegroundToggled</c>).
/// </summary>
public sealed class ProfileOverrideMergeTests
{
    // The discovery probe behind "pwsh" as PowerShellProbe reports it. The
    // ProbeId is load-bearing in the assertions below that say an override
    // merged onto discovery rather than replacing it.
    private static readonly DiscoveredProfile s_pwsh = new(
        Id: "pwsh",
        Name: "PowerShell",
        Command: @"pwsh.exe",
        ProbeId: "pwsh");

    private static ResolvedProfileSet Resolve(
        string configText, params DiscoveredProfile[] discovered)
    {
        var view = ConfigServiceProfileParser.ParseAll(configText, _ => null);
        return ProfileOrderResolver.Resolve(
            user: [.. view.ParsedProfiles.Values],
            overrides: view.ProfileOverrides,
            discovered: discovered,
            profileOrder: view.ProfileOrder,
            defaultProfileId: view.DefaultProfileId,
            hiddenIds: view.HiddenProfileIds);
    }

    private static ResolvedProfile Single(string configText, params DiscoveredProfile[] discovered)
        => Assert.Single(Resolve(configText, discovered).Visible);

    [Fact]
    public void The_icon_the_page_wrote_lands_on_the_discovered_profile()
    {
        // What OnProfileIconChanged persists for an MDL2 pick: one subkey,
        // no name, no command.
        //
        // Deliberately not "brand:pwsh" for a pwsh profile: that is what
        // ResolveFallbackIcon already derives from the command basename, so
        // it comes out the same whether the override merged or was dropped,
        // and the test would pass on the bug it is here to catch.
        var resolved = Single("profile.pwsh.icon = mdl2:E756", s_pwsh);

        Assert.Equal(0xE756, Assert.IsType<IconSpec.Mdl2Token>(resolved.Icon).CodePoint);
    }

    [Fact]
    public void ABrandTheShellDoesNotMapTo_StillLands()
    {
        // The ordinary case: a brand picked off the grid, which for a pwsh
        // profile is never the brand the exe maps to.
        var resolved = Single("profile.pwsh.icon = brand:git", s_pwsh);

        Assert.Equal(new IconSpec.BrandKey("git", null), resolved.Icon);
    }

    [Fact]
    public void The_tracks_freeze_the_page_wrote_lands_on_the_discovered_profile()
    {
        // What OnTracksForegroundToggled persists when the switch is turned
        // off: the "on" branch removes the key instead, so only the false
        // marker is ever written.
        var resolved = Single("profile.pwsh.tab-icon-tracks-foreground = false", s_pwsh);

        Assert.False(resolved.TabIconTracksForeground);
    }

    [Fact]
    public void The_override_leaves_discovery_supplying_the_rest()
    {
        var resolved = Single("profile.pwsh.icon = mdl2:E756", s_pwsh);

        Assert.Equal("PowerShell", resolved.Name);
        Assert.Equal(@"pwsh.exe", resolved.Command);
        // The load-bearing one: ProbeId is what the active-process tracker
        // probes, and a profile that reaches the tracker without it never
        // changes its tab icon. An override must not be a redefinition.
        Assert.Equal("pwsh", resolved.ProbeId);
    }

    [Fact]
    public void A_partial_block_may_rename_without_redefining()
    {
        // name + icon, no command: name is the one key discovery can be
        // overridden on without giving up command.
        var resolved = Single("profile.pwsh.name = My Shell\nprofile.pwsh.icon = mdl2:E756", s_pwsh);

        Assert.Equal("My Shell", resolved.Name);
        Assert.Equal(@"pwsh.exe", resolved.Command);
        Assert.Equal(0xE756, Assert.IsType<IconSpec.Mdl2Token>(resolved.Icon).CodePoint);
    }

    [Fact]
    public void A_partial_block_for_an_id_nothing_discovered_for_resolves_to_nothing()
    {
        // Same shape, no discovery answer: there is nothing to merge onto,
        // so no profile appears and the warning stands.
        var view = ConfigServiceProfileParser.ParseAll(
            "profile.ghost.icon = brand:pwsh", _ => null);

        var resolved = ProfileOrderResolver.Resolve(
            user: [.. view.ParsedProfiles.Values],
            overrides: view.ProfileOverrides,
            discovered: Array.Empty<DiscoveredProfile>(),
            profileOrder: view.ProfileOrder,
            defaultProfileId: view.DefaultProfileId,
            hiddenIds: view.HiddenProfileIds);

        Assert.Empty(resolved.Visible);
        Assert.Empty(resolved.MergedOverrideIds);
        Assert.Single(view.ProfileWarnings);
        Assert.Contains("ghost", view.ProfileWarnings[0]);
    }

    [Fact]
    public void A_partial_block_is_kept_as_an_override_not_dropped_on_the_floor()
    {
        // The parser's own contract: a bag that is not a definition is not
        // in Profiles, and it is not gone either.
        var parsed = ProfileSourceParser.Parse("profile.pwsh.icon = mdl2:E756");

        Assert.Empty(parsed.Profiles);
        var o = Assert.Single(parsed.Overrides).Value;
        Assert.Equal("pwsh", o.Id);
        Assert.Null(o.Name);
        Assert.Null(o.Command);
        Assert.Equal(0xE756, Assert.IsType<IconSpec.Mdl2Token>(o.Icon).CodePoint);
    }

    [Fact]
    public void A_partial_block_that_says_nothing_about_tracks_leaves_the_default_alone()
    {
        // Trinary all the way: an icon override must not be read as "and
        // stop tracking the foreground process".
        var parsed = ProfileSourceParser.Parse("profile.pwsh.icon = mdl2:E756");

        Assert.Null(parsed.Overrides["pwsh"].TabIconTracksForeground);
        Assert.True(Single("profile.pwsh.icon = mdl2:E756", s_pwsh).TabIconTracksForeground);
    }

    [Fact]
    public void A_hidden_only_block_is_an_override_marker_and_never_a_profile()
    {
        // The existing hidden-marker path now travels as an override; it
        // must still not define a profile, and must still not warn.
        var view = ConfigServiceProfileParser.ParseAll("profile.pwsh.hidden = true", _ => null);

        Assert.Empty(view.ParsedProfiles);
        Assert.Contains("pwsh", view.ProfileOverrides.Keys);
        Assert.Empty(view.ProfileWarnings);
    }

    [Fact]
    public void A_full_block_still_wins_over_discovery_outright()
    {
        // The other half of the contract, unchanged: a block that spells
        // name and command is a definition, and a definition replaces the
        // discovered profile rather than merging with it.
        var resolved = Single(
            "profile.pwsh.name = Mine\nprofile.pwsh.command = C:\\pwsh.exe", s_pwsh);

        Assert.Equal("Mine", resolved.Name);
        Assert.Equal(@"C:\pwsh.exe", resolved.Command);
        Assert.Null(resolved.ProbeId);
    }

    [Fact]
    public void A_partial_block_does_not_move_the_profile_in_the_list()
    {
        // The merge has to happen where the discovered def is added, not in
        // the user pass: an override that reordered the row would make every
        // icon pick shuffle the list.
        var resolved = Resolve(
            "profile.pwsh.icon = mdl2:E756",
            s_pwsh,
            new(Id: "cmd", Name: "Command Prompt", Command: "cmd.exe", ProbeId: "cmd"));

        Assert.Equal(new[] { "cmd", "pwsh" }, resolved.Visible.Select(p => p.Id));
    }
}