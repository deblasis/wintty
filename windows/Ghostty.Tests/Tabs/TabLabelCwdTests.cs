using Ghostty.Core;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The tab label at a shell prompt: the folder the shell reported, not the
/// profile name and not the console's default exe-path title.
/// </summary>
public class TabLabelCwdTests
{
    private static ProfileSnapshot NamedProfile(string name) =>
        ProfileSnapshotStore.From(
            new ResolvedProfile(
                Id: "p", Name: name, Command: "pwsh.exe",
                WorkingDirectory: null, Icon: new IconSpec.BundledKey("pwsh"),
                TabTitle: name, Visuals: EffectiveVisualOverrides.Empty,
                ProbeId: null, OrderIndex: 0, IsDefault: true),
            version: 1);

    [Theory]
    [InlineData(@"C:\temp\wintty-fx-cwd", "wintty-fx-cwd")]
    [InlineData(@"C:\temp\wintty-fx-cwd\", "wintty-fx-cwd")]
    [InlineData("c:/Users/alex", "alex")]
    [InlineData(@"\\server\share", "share")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\alex", "alex")]
    [InlineData(@"C:\", "C:")]
    public void FolderName_IsTheLastSegment(string cwd, string expected)
        => Assert.Equal(expected, TabLabel.FolderName(cwd));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\")]
    [InlineData("/")]
    public void FolderName_IsNull_WhenThereIsNothingToName(string? cwd)
        => Assert.Null(TabLabel.FolderName(cwd));

    [Theory]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"C:\Windows\system32\cmd.exe")]
    [InlineData("c:/Windows/system32/cmd.exe")]
    [InlineData(@"\\server\share\sh.exe")]
    public void Meaningful_DropsTheConsolesDefaultExePathTitle(string title)
        => Assert.Null(TabLabel.Meaningful(title));

    [Theory]
    [InlineData("vim file.txt")]
    [InlineData(@"vim C:\src\x.zig")]
    [InlineData("alex@box: ~/src")]
    [InlineData("make -j8")]
    public void Meaningful_KeepsATitleThatSaysSomething(string title)
        => Assert.Equal(title, TabLabel.Meaningful(title));

    [Fact]
    public void EffectiveTitle_IsTheFolder_WhenTheShellOnlyReportsItsOwnPath()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(NamedProfile("Primary"));

        // What a stock pwsh tab looks like before the fix: the profile name
        // until ConPTY's default title arrives, then the exe path.
        Assert.Equal("Primary", tab.EffectiveTitle);
        tab.ShellReportedTitle = @"C:\Program Files\PowerShell\7\pwsh.exe";
        Assert.Equal("Primary", tab.EffectiveTitle);

        tab.ShellReportedCwd = @"C:\temp\wintty-fx-cwd";
        Assert.Equal("wintty-fx-cwd", tab.EffectiveTitle);
    }

    [Fact]
    public void EffectiveTitle_LetsARealShellTitleBeatTheFolder()
    {
        var tab = new TabModel(new FakePaneHost()) { ShellReportedCwd = @"C:\src\repo" };
        Assert.Equal("repo", tab.EffectiveTitle);

        tab.ShellReportedTitle = "vim build.zig";
        Assert.Equal("vim build.zig", tab.EffectiveTitle);

        // And the user's own name still beats everything.
        tab.UserOverrideTitle = "notes";
        Assert.Equal("notes", tab.EffectiveTitle);
    }

    [Fact]
    public void EffectiveTitle_NeverRendersEmpty_WhenNoCwdIsKnown()
    {
        var tab = new TabModel(new FakePaneHost());
        Assert.Equal(AppIdentity.ProductName, tab.EffectiveTitle);

        tab.ShellReportedCwd = "   ";
        Assert.Equal(AppIdentity.ProductName, tab.EffectiveTitle);
    }

    [Fact]
    public void ShellReportedCwd_RaisesEffectiveTitle()
    {
        var tab = new TabModel(new FakePaneHost());
        var raised = 0;
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabModel.EffectiveTitle)) raised++;
        };

        tab.ShellReportedCwd = @"C:\src\repo";
        Assert.Equal(1, raised);

        // The equality guard holds: an unchanged report is not a relabel.
        tab.ShellReportedCwd = @"C:\src\repo";
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ACdNeverOverwritesTheNameTheUserTyped()
    {
        // Rename Tab writes UserOverrideTitle (TabContextMenuBuilder), which
        // sits above every derived tier. A shell walking around the disk must
        // not take the name back.
        var tab = new TabModel(new FakePaneHost())
        {
            ShellReportedCwd = @"C:\src\repo",
            UserOverrideTitle = "deploy",
        };
        Assert.Equal("deploy", tab.EffectiveTitle);

        tab.ShellReportedCwd = @"C:\src\other";
        Assert.Equal("deploy", tab.EffectiveTitle);

        tab.ShellReportedTitle = "vim x.zig";
        Assert.Equal("deploy", tab.EffectiveTitle);

        // Clearing the override hands the label back to the derived chain.
        tab.UserOverrideTitle = null;
        Assert.Equal("vim x.zig", tab.EffectiveTitle);
    }

    [Fact]
    public void TheHostsCwdReport_ReachesTheTabLabel()
    {
        // The wiring the strip depends on: TabManager subscribes the tab to
        // its pane host's CwdChanged, so a shell's OSC 7 relabels the tab
        // without anyone reading the leaf.
        var host = new FakePaneHost();
        var mgr = new TabManager((_) => host);
        var tab = mgr.Tabs[0];

        host.RaiseCwdChanged(@"C:\temp\wintty-fx-cwd");

        Assert.Equal(@"C:\temp\wintty-fx-cwd", tab.ShellReportedCwd);
        Assert.Equal("wintty-fx-cwd", tab.EffectiveTitle);
    }
}
