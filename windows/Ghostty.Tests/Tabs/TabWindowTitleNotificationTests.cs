using System;
using System.Collections.Generic;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The window caption is TOLD when the active tab's label moves.
///
/// <c>TabTitleSurfacesTests</c> guards the other half: that every surface
/// READS <c>EffectiveTitle</c> rather than composing its own string. The two
/// halves are not the same claim and neither implies the other. The window
/// title is the one surface that does not re-read on its own -- it is a
/// Win32 caption written once per notification, from
/// <c>TitleBarCoordinator</c>'s <c>WindowTitleChanged</c> handler -- so a
/// tier that composes correctly and notifies nothing leaves the strip, the
/// tooltip and the accessible name moving while Alt-Tab and the taskbar stay
/// on the value they had. Reading alone cannot catch that, and did not: the
/// launch-name tier was added, every surfaces test stayed green, and the
/// caption lagged.
///
/// So this walks the tiers by behaviour rather than by source.
///
/// What it does NOT catch, said plainly because the obvious reading is
/// wrong: <see cref="Tiers"/> is hand-maintained, so a tier added to
/// <c>TabModel.Compose</c> and never listed here passes silently -- the
/// <c>default: throw</c> only catches the reverse. Routing the caption on
/// the label rather than on an input list is what makes that narrow: any
/// tier whose setter calls <c>RaiseTitleDerived</c> is carried whether or
/// not anyone remembered this file. The residual is a new <c>Compose</c>
/// input whose author forgets that call, which is a smaller and more local
/// mistake than the one this file was written for.
/// </summary>
public class TabWindowTitleNotificationTests
{
    private static ProfileSnapshot NamedProfile(string name) =>
        ProfileSnapshotStore.From(
            new ResolvedProfile(
                Id: "p", Name: name, Command: "pwsh.exe",
                WorkingDirectory: null, Icon: new IconSpec.BundledKey("pwsh"),
                TabTitle: name, Visuals: EffectiveVisualOverrides.Empty,
                ProbeId: null, OrderIndex: 0, IsDefault: true),
            version: 1);

    private static (TabManager Manager, List<string> Captions) NewWindow()
    {
        var manager = new TabManager(_ => new FakePaneHost());
        var captions = new List<string>();
        // Exactly what TitleBarCoordinator does with the event.
        manager.WindowTitleChanged += (_, _) => captions.Add(manager.ActiveTab.WordTitle);
        return (manager, captions);
    }

    /// <summary>
    /// Every tier that can move the label. A tier named here and not
    /// driven below is a compile-time hole, not a silent pass.
    ///
    /// The tiers are named rather than passed as delegates because
    /// <c>TabModel</c> is internal and an xunit theory's parameters have
    /// to be public.
    /// </summary>
    public static TheoryData<string> Tiers() =>
    [
        "launch name",
        "profile name",
        "reported directory",
        "shell title",
        "user rename",
    ];

    private static string Drive(string tier, TabModel tab)
    {
        switch (tier)
        {
            case "launch name":
                tab.OnPaneLaunched("pwsh.exe", "pwsh.exe");
                return "PowerShell";
            case "profile name":
                tab.AttachProfileSnapshot(NamedProfile("Build"));
                return "Build";
            case "reported directory":
                tab.ShellReportedCwd = @"C:\src\repo";
                return "repo";
            case "shell title":
                tab.ShellReportedTitle = "vim build.zig";
                return "vim build.zig";
            case "user rename":
                tab.UserOverrideTitle = "notes";
                return "notes";
            default:
                throw new ArgumentOutOfRangeException(nameof(tier), tier, "unhandled tier");
        }
    }

    /// <summary>
    /// Each tier driven on its own onto a tab that has nothing above it.
    /// The caption has to arrive at the same word the label shows, once
    /// per change.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tiers))]
    public void TheCaptionIsTold_WheneverTheActiveTabsLabelMoves(string tier)
    {
        var (manager, captions) = NewWindow();
        var tab = manager.ActiveTab;

        var expected = Drive(tier, tab);

        Assert.Equal(expected, tab.WordTitle);
        Assert.Equal([expected], captions);
    }

    /// <summary>
    /// The regression this file exists for, spelled out: a cold start with
    /// no profile, no directory and no title. The caption starts on the
    /// generic word and has to follow the label onto the launch name.
    /// </summary>
    [Fact]
    public void TheCaption_FollowsTheLabel_OntoTheLaunchName()
    {
        var (manager, captions) = NewWindow();
        var tab = manager.ActiveTab;
        Assert.Equal(TabLabel.UnnamedTab, tab.WordTitle);

        tab.OnPaneLaunched("btop.exe", "btop.exe --preset 0");

        Assert.Equal("btop", tab.WordTitle);
        Assert.Equal(["btop"], captions);
    }

    /// <summary>
    /// A background tab's label is not the caption. Only the active tab
    /// names the window.
    /// </summary>
    [Fact]
    public void ABackgroundTabsLaunchName_DoesNotTouchTheCaption()
    {
        var (manager, captions) = NewWindow();
        var background = manager.ActiveTab;
        manager.NewTab();
        captions.Clear();

        background.OnPaneLaunched("pwsh.exe", "pwsh.exe");

        Assert.Empty(captions);
    }

    /// <summary>
    /// A write that leaves an input where it was is not a change. The
    /// caption is a Win32 call and the accessible name is an
    /// announcement; repeating either for a no-op write is noise a
    /// listener hears.
    ///
    /// The contract is about the INPUTS, not about the composed label. A
    /// lower tier moving under a higher one that still outranks it does
    /// notify, here as everywhere else in the model (a reported directory
    /// under a live shell title has always done the same), and the
    /// coordinator writes the same caption string back. Making that the
    /// stricter "notify only when the label actually moved" is a change
    /// to every tier, not to this one, and does not belong in this PR.
    /// </summary>
    [Fact]
    public void AWriteThatChangesNoInput_TellsTheCaptionNothing()
    {
        var (manager, captions) = NewWindow();
        var tab = manager.ActiveTab;
        tab.ShellReportedTitle = "vim build.zig";
        tab.OnPaneLaunched("pwsh.exe", "pwsh.exe");
        captions.Clear();

        tab.ShellReportedTitle = "vim build.zig";
        tab.OnPaneLaunched("pwsh.exe", "pwsh.exe");

        Assert.Empty(captions);
    }
}
