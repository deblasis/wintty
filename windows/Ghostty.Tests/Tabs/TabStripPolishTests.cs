using System.Collections.Generic;
using Ghostty.Core;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The strip's second look: a home tab draws a glyph and is called "Home"
/// where only words fit; a hover says something only when it adds to the
/// label; a long path keeps its root and its tail; a tab that has not
/// spoken yet shows the app's own icon while it starts.
/// </summary>
public class TabStripPolishTests
{
    private const string Home = @"C:\Users\alex";

    private static ProfileSnapshot Profile(string name, string command) =>
        ProfileSnapshotStore.From(
            new ResolvedProfile(
                Id: "p", Name: name, Command: command,
                WorkingDirectory: null, Icon: new IconSpec.BundledKey("pwsh"),
                TabTitle: name, Visuals: EffectiveVisualOverrides.Empty,
                ProbeId: null, OrderIndex: 0, IsDefault: true),
            version: 1);

    // --- words for surfaces that cannot draw a glyph ---

    [Theory]
    [InlineData("~", "Home")]
    [InlineData("repo", "repo")]
    [InlineData("~tmp", "~tmp")]
    public void Word_SaysHomeForTheTilde_AndLeavesEverythingElse(string title, string expected)
        => Assert.Equal(expected, TabLabel.Word(title));

    [Fact]
    public void TheWordTitle_FollowsTheLabel()
    {
        var tab = new TabModel(new FakePaneHost()) { HomeDirectory = Home };
        tab.ShellReportedCwd = Home;
        Assert.Equal("~", tab.EffectiveTitle);
        Assert.True(tab.IsHome);
        Assert.Equal("Home", tab.WordTitle);

        tab.ShellReportedCwd = @"C:\Users\alex\src";
        Assert.False(tab.IsHome);
        Assert.Equal("src", tab.WordTitle);
    }

    // --- a hover that adds nothing is no hover ---

    [Fact]
    public void TheHover_IsNull_WhenItWouldOnlyRepeatTheLabel()
    {
        var tab = new TabModel(new FakePaneHost());
        Assert.Null(tab.HoverText);

        tab.UserOverrideTitle = "deploy";
        Assert.Null(tab.HoverText);

        tab.AttachProfileSnapshot(Profile("Primary", "pwsh.exe"));
        tab.UserOverrideTitle = null;
        Assert.Equal("Primary", tab.EffectiveTitle);
        Assert.Null(tab.HoverText);
    }

    [Fact]
    public void TheHover_IsTheFullTooltip_WhenItAddsSomething()
    {
        var tab = new TabModel(new FakePaneHost()) { HomeDirectory = Home };
        tab.ShellReportedCwd = @"C:\Users\alex\src\repo";
        Assert.Equal(@"~\src\repo", tab.HoverText);
        Assert.Equal(tab.TooltipText, tab.HoverText);

        // At home the label is one glyph and the hover is the real place.
        tab.ShellReportedCwd = Home;
        Assert.Equal(Home, tab.HoverText);
    }

    [Fact]
    public void TheHover_IsRaised_WithTheTooltip()
    {
        var tab = new TabModel(new FakePaneHost());
        var raised = 0;
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TabModel.HoverText)) raised++; };
        tab.ShellReportedCwd = @"C:\src\repo";
        Assert.Equal(1, raised);
    }

    // --- long paths keep the root and the tail ---

    [Theory]
    [InlineData(@"~\src\repo", 60, @"~\src\repo")]
    [InlineData(@"~\code\projects\customers\acme\services\billing\src\Billing.Api", 40, @"~\…\services\billing\src\Billing.Api")]
    [InlineData(@"C:\very\deep\tree\of\directories\that\keeps\on\going\forever\leaf", 40, @"C:\…\that\keeps\on\going\forever\leaf")]
    [InlineData(@"\\server\share\projects\alpha\beta\gamma\delta\epsilon\zeta", 40, @"\\server\share\…\delta\epsilon\zeta")]
    [InlineData("~/code/projects/customers/acme/services/billing/src/Billing.Api", 40, "~/…/services/billing/src/Billing.Api")]
    [InlineData(@"C:\a\b", 60, @"C:\a\b")]
    // The early-out's own boundary: a path exactly at budget is untouched.
    [InlineData(@"C:\abcdefg", 10, @"C:\abcdefg")]
    // A trailing separator names no segment and is not charged for.
    [InlineData(@"C:\abcdefg\", 10, @"C:\abcdefg")]
    // The UNC root is two segments: one segment past the share is not
    // abbreviated at all, which is the only place that count is observable.
    [InlineData(@"\\server\share\alpha", 10, @"\\server\share\alpha")]
    // A root on its own has nothing to elide.
    [InlineData(@"C:\", 2, @"C:")]
    [InlineData("~", 0, "~")]
    // The extended-length UNC spelling is the same share.
    [InlineData(@"\\?\UNC\server\share\a\b\c\leaf", 20, @"\\server\share\…\leaf")]
    // A rooted POSIX path keeps the separator that makes it absolute.
    [InlineData("/home/alex/code/deep/tree/leaf", 20, "/home/…/tree/leaf")]
    public void Abbreviate_KeepsTheRootAndAsManyTailSegmentsAsFit(string path, int max, string expected)
        => Assert.Equal(expected, TabLabel.Abbreviate(path, max));

    [Fact]
    public void Abbreviate_AlwaysKeepsTheLastSegment_EvenWhenItAloneIsTooLong()
        => Assert.Equal(@"C:\…\a-single-segment-longer-than-the-whole-budget",
            TabLabel.Abbreviate(@"C:\x\y\a-single-segment-longer-than-the-whole-budget", 20));

    [Fact]
    public void TheTooltip_AbbreviatesTheDirectoryLine_NotTheTitle()
    {
        var tab = new TabModel(new FakePaneHost()) { HomeDirectory = Home };
        tab.ShellReportedCwd = @"C:\Users\alex\code\projects\customers\acme\services\billing\src\Billing.Api";
        tab.ShellReportedTitle = "vim a-rather-long-file-name-that-should-stay-whole.zig";
        // The collapsed path is 63 characters; the budget is 60, so exactly
        // the first segment after home goes.
        Assert.Equal(
            "vim a-rather-long-file-name-that-should-stay-whole.zig\n~\\…\\projects\\customers\\acme\\services\\billing\\src\\Billing.Api",
            tab.TooltipText);
        // What the menu copies is never abbreviated.
        Assert.EndsWith(@"\Billing.Api", tab.ActionableCwd);
        Assert.DoesNotContain("…", tab.ActionableCwd);
    }

    // --- a tab that has not spoken yet ---

    /// <summary>
    /// The icon view-model must announce the swap in both directions, or a
    /// presenter that only listens keeps whatever it drew first: every tab
    /// stuck on the app's icon for the life of the window.
    /// </summary>
    [Fact]
    public void TheIconViewModel_AnnouncesTheStartAndItsEnd()
    {
        var vm = new TabIconViewModel(new IconSpec.BrandKey("pwsh", null), "PowerShell");
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SetSettling(true);
        Assert.Contains(nameof(TabIconViewModel.Icon), raised);
        Assert.Contains(nameof(TabIconViewModel.TooltipText), raised);
        Assert.Equal(new IconSpec.BundledKey("default"), vm.Icon);

        raised.Clear();
        vm.SetSettling(false);
        Assert.Contains(nameof(TabIconViewModel.Icon), raised);
        Assert.Contains(nameof(TabIconViewModel.TooltipText), raised);
        Assert.Equal(new IconSpec.BrandKey("pwsh", null), vm.Icon);
        Assert.Equal("PowerShell", vm.TooltipText);
    }

    [Fact]
    public void TheIconViewModel_AnnouncesTheDerivedGlyphFlags_WhenTheKindChanges()
    {
        // A profile drawn from a glyph becomes an image while starting, so
        // the flags a presenter switches on move too.
        var vm = new TabIconViewModel(new IconSpec.Mdl2Token(0xE700), "Menu");
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SetSettling(true);
        Assert.Contains(nameof(TabIconViewModel.IsMdl2Glyph), raised);
        Assert.Contains(nameof(TabIconViewModel.Mdl2CodePoint), raised);
        Assert.False(vm.IsMdl2Glyph);
    }

    [Fact]
    public void TheStart_OutranksAForegroundOverride()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(Profile("Primary", "pwsh.exe"));
        tab.BeginSettling();

        // A real override: an exe the table maps that is not the launch shell.
        tab.OnActiveProcessChanged("vim.exe", "vim x.zig");
        Assert.Equal(new IconSpec.BundledKey("default"), tab.TabIcon.Icon);
        Assert.StartsWith("Starting…", tab.TabIcon.TooltipText);

        // ... and it is waiting underneath when the start ends.
        tab.Settle();
        Assert.Equal(new IconSpec.BrandKey("vim", null), tab.TabIcon.Icon);
        Assert.Equal("Vim in PowerShell", tab.TabIcon.TooltipText);
    }

    [Fact]
    public void TheStartingTooltip_KeepsSayingWhichProfileItIs()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(Profile("Primary", "pwsh.exe"));
        tab.BeginSettling();
        Assert.Equal("Starting…\nPowerShell\nPrimary", tab.TabIcon.TooltipText);
    }

    [Fact]
    public void AnAdoptedTabsFirstRender_StillSettlesIt()
    {
        // The adopter re-wires the bridge, so a tab that arrives mid-start
        // (a restore) is not left starting forever.
        var host = new FakePaneHost();
        var adopted = new TabModel(host);
        adopted.BeginSettling();
        var mgr = new TabManager(_ => new FakePaneHost(), homeDirectory: Home);
        mgr.AdoptTab(adopted);

        host.RaiseFirstRendered();
        Assert.False(adopted.IsSettling);
    }

    [Fact]
    public void ATabBuiltDirectly_IsNotSettling()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(Profile("Primary", "pwsh.exe"));
        Assert.False(tab.IsSettling);
        Assert.Equal(new IconSpec.BundledKey("pwsh"), tab.TabIcon.Icon);
    }

    [Fact]
    public void WhileSettling_TheIconIsTheApps_AndTheLabelIsUnchanged()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(Profile("Primary", "pwsh.exe"));
        tab.BeginSettling();

        Assert.True(tab.IsSettling);
        Assert.Equal(new IconSpec.BundledKey("default"), tab.TabIcon.Icon);
        Assert.StartsWith("Starting…", tab.TabIcon.TooltipText);
        Assert.Equal("Primary", tab.EffectiveTitle);
    }

    [Fact]
    public void TheFirstRender_SettlesTheTab_AndTheProfileIconReturns()
    {
        var host = new FakePaneHost();
        var mgr = new TabManager(
            _ => host, initialSnapshot: Profile("Primary", "pwsh.exe"), homeDirectory: Home);
        var tab = mgr.Tabs[0];
        Assert.True(tab.IsSettling);
        Assert.Equal(new IconSpec.BundledKey("default"), tab.TabIcon.Icon);

        var raised = 0;
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TabModel.IsSettling)) raised++; };
        host.RaiseFirstRendered();

        Assert.False(tab.IsSettling);
        Assert.Equal(new IconSpec.BundledKey("pwsh"), tab.TabIcon.Icon);
        Assert.Equal("PowerShell\nPrimary", tab.TabIcon.TooltipText);
        Assert.Equal(1, raised);

        // A second render is not a second event.
        host.RaiseFirstRendered();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void AShellThatReportsATitle_SettlesTheTab_BeforeAnyRender()
    {
        var mgr = new TabManager(_ => new FakePaneHost(), homeDirectory: Home);
        var tab = mgr.Tabs[0];
        Assert.True(tab.IsSettling);

        tab.ShellReportedTitle = "vim x.zig";
        Assert.False(tab.IsSettling);
    }

    [Fact]
    public void AShellThatReportsADirectory_SettlesTheTab_BeforeAnyRender()
    {
        var host = new FakePaneHost();
        var mgr = new TabManager(_ => host, homeDirectory: Home);
        var tab = mgr.Tabs[0];
        Assert.True(tab.IsSettling);

        host.RaiseCwdChanged(@"C:\src\repo");
        Assert.False(tab.IsSettling);
    }

    [Fact]
    public void AssistiveClients_AreToldATabIsStarting()
    {
        var mgr = new TabManager(_ => new FakePaneHost(), homeDirectory: Home);
        var tab = mgr.Tabs[0];
        Assert.Equal("Starting", TabAccessibleText.Status(tab));

        tab.Settle();
        Assert.Equal("", TabAccessibleText.Status(tab));
    }

    [Fact]
    public void ATabTitledTilde_ByItsShell_IsNotAHomeTab()
    {
        // A prompt that titles the window "~" is not a claim about the
        // directory, and a tab drawn as a bare house would have no name and
        // nothing to hover.
        var tab = new TabModel(new FakePaneHost()) { HomeDirectory = Home };
        tab.ShellReportedCwd = @"C:\src\repo";
        tab.ShellReportedTitle = "~";

        Assert.Equal("~", tab.EffectiveTitle);
        Assert.False(tab.IsHome);
        Assert.Equal("~", tab.WordTitle);
        Assert.Equal("~", TabAccessibleText.Name(tab));
        Assert.NotNull(tab.HoverText);
    }

    [Fact]
    public void ATabRenamedTilde_IsNotAHomeTabEither()
    {
        var tab = new TabModel(new FakePaneHost()) { HomeDirectory = Home };
        tab.ShellReportedCwd = Home;
        Assert.True(tab.IsHome);

        tab.UserOverrideTitle = "~";
        Assert.False(tab.IsHome);
    }

    [Fact]
    public void ANewTabFromTheManager_StartsSettling_AndAnAdoptedOneDoesNot()
    {
        var mgr = new TabManager(_ => new FakePaneHost(), homeDirectory: Home);
        Assert.True(mgr.NewTab().IsSettling);

        var adopted = new TabModel(new FakePaneHost());
        mgr.AdoptTab(adopted);
        Assert.False(adopted.IsSettling);
    }

    [Fact]
    public void TheForegroundTracker_DoesNotUnsettleATab()
    {
        // A process arriving is not the shell speaking; only a render or a
        // report ends the start.
        var mgr = new TabManager(
            _ => new FakePaneHost(),
            initialSnapshot: Profile("Primary", "pwsh.exe"),
            homeDirectory: Home);
        var tab = mgr.Tabs[0];
        tab.OnActiveProcessChanged("vim.exe", "vim x.zig");
        Assert.True(tab.IsSettling);
        Assert.Equal(new IconSpec.BundledKey("default"), tab.TabIcon.Icon);
    }
}
